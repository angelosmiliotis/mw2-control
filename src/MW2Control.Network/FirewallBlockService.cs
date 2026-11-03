using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MW2Control.Network;

public sealed record FirewallRule(string Name, string Group, string Address, string GamePath, int Direction,
    int Protocol = 17, int Action = 0, bool Enabled = true, int Profiles = int.MaxValue,
    string LocalAddresses = "*", string LocalPorts = "*", string RemotePorts = "*", string InterfaceTypes = "All", string ServiceName = "", bool EdgeTraversal = false);
public interface IFirewallPolicy
{
    IReadOnlyList<FirewallRule> ReadRules();
    void Add(FirewallRule rule);
    void Remove(string name);
}

public static class FirewallValidation
{
    public static string PublicAddress(string text, IEnumerable<IPAddress>? localAddresses = null)
    {
        if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out var ip) || !string.Equals(text, ip.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select one exact public IP address; CIDR, wildcards, zones and alternate numeric forms are rejected.");
        if (localAddresses?.Contains(ip) == true) throw new ArgumentException("Local/interface/gateway addresses cannot be blocked.");
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            int a = bytes[0], b = bytes[1], c = bytes[2];
            if (a == 0 || a == 10 || a == 127 || a >= 224 || (a == 100 && b is >= 64 and <= 127) ||
                (a == 169 && b == 254) || (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && (b == 168 || b == 0 || (b == 88 && c == 99))) ||
                (a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) ||
                (a == 203 && b == 0 && c == 113))
                throw new ArgumentException("Only public unicast peers are permitted; private, special and limited broadcast addresses are rejected.");
        }
        else if (ip.AddressFamily != AddressFamily.InterNetworkV6 || (bytes[0] & 0xe0) != 0x20 || ip.ScopeId != 0 ||
            (bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || (bytes[2] == 0x0d && bytes[3] == 0xb8))) ||
            (bytes[0] == 0x20 && bytes[1] == 0x02))
            throw new ArgumentException("Only global unicast IPv6 peers are permitted; local, mapped and special ranges are rejected.");
        return ip.ToString();
    }
    public static string GamePath(string path, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(path), "iw4mp.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A real absolute local iw4mp.exe path is required.");
        var full = Path.GetFullPath(path);
        if (!fileExists(full)) throw new ArgumentException("The selected iw4mp.exe file does not exist.");
        return full;
    }
}

public sealed class FirewallBlockService
{
    public const string Group = "MW2 CONTROL — owned UDP peer blocks v1";
    public const string Prefix = "MW2Control.PeerBlock.v1.";
    private static readonly object Gate = new();
    private readonly IFirewallPolicy policy;
    private readonly Func<string, bool> fileExists;
    private readonly Func<IEnumerable<IPAddress>> locals;
    public FirewallBlockService() : this(new ComFirewallPolicy(), File.Exists, null) { }
    public FirewallBlockService(IFirewallPolicy policy, Func<string, bool>? fileExists = null, Func<IEnumerable<IPAddress>>? localAddresses = null)
    { this.policy = policy; this.fileExists = fileExists ?? File.Exists; locals = localAddresses ?? GetLocalAddresses; }
    private static IEnumerable<IPAddress> GetLocalAddresses() => NetworkInterface.GetAllNetworkInterfaces().SelectMany(n =>
    {
        var properties = n.GetIPProperties();
        return properties.UnicastAddresses.Select(a => a.Address).Concat(properties.GatewayAddresses.Select(g => g.Address));
    }).ToArray();
    public static string BlockId(string address, string gamePath) => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address + "\n" + gamePath.ToUpperInvariant())))[..24];
    private static FirewallRule Expected(string address, string path, int direction) => new(BlockId(address, path) + (direction == 1 ? ".in" : ".out"), Group, address, path, direction);
    private static bool Same(FirewallRule a, FirewallRule b) => a with { GamePath = a.GamePath.ToUpperInvariant() } == b with { GamePath = b.GamePath.ToUpperInvariant() };
    private bool IsOwned(FirewallRule rule)
    {
        try
        {
            // Listing/unblocking remains possible after the executable is removed, but its absolute identity must still validate.
            var path = FirewallValidation.GamePath(rule.GamePath, _ => true);
            // Validate persisted scope, independent of the current interface/gateway configuration.
            // Network changes must never prevent removal of an otherwise valid owned rule.
            var address = FirewallValidation.PublicAddress(rule.Address);
            return rule.Direction is 1 or 2 && Same(rule, Expected(address, path, rule.Direction));
        }
        catch { return false; }
    }
    private IReadOnlyList<FirewallRule> Owned(IReadOnlyList<FirewallRule> all)
    {
        var candidates = all.Where(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal) || r.Group == Group).ToArray();
        if (candidates.Any(r => !IsOwned(r) || all.Count(other => other.Name == r.Name) != 1))
            throw new InvalidOperationException("Conflicting or ambiguous app-owned firewall entries found; no changes made.");
        return candidates;
    }
    public IReadOnlyList<FirewallBlock> ListOwnedBlocks()
    {
        lock (Gate) return Owned(policy.ReadRules()).GroupBy(r => BlockId(r.Address, r.GamePath))
            .Select(g => new FirewallBlock(g.Key, g.First().Address, g.First().GamePath,
                $"Owned UDP block ({g.Count()}/2 directions). Blocking a relay can affect other lobbies." )).ToArray();
    }
    public NetworkResult Block(string address, string gamePath, bool explicitlyConfirmed)
    {
        if (!explicitlyConfirmed) return new(false, "Explicit confirmation is required; blocking can disconnect the lobby and affect other lobbies using this relay/IP.");
        lock (Gate)
        {
            var added = new List<FirewallRule>();
            try
            {
                address = FirewallValidation.PublicAddress(address, locals());
                gamePath = FirewallValidation.GamePath(gamePath, fileExists);
                var all = policy.ReadRules();
                Owned(all);
                foreach (var direction in new[] { 1, 2 })
                {
                    var expected = Expected(address, gamePath, direction);
                    var matching = all.Where(r => r.Name == expected.Name).ToArray();
                    if (matching.Length == 1 && Same(matching[0], expected)) continue;
                    if (matching.Length != 0) throw new InvalidOperationException("A conflicting firewall entry already exists.");
                    added.Add(expected);
                    policy.Add(expected);
                    var observed = policy.ReadRules().Where(r => r.Name == expected.Name).ToArray();
                    if (observed.Length != 1 || !Same(observed[0], expected)) throw new InvalidOperationException("Firewall block readback failed.");
                }
                return new(true, "Inbound and outbound UDP blocks are present for this exact IP and iw4mp.exe. Host identity and future IPs are not guaranteed.");
            }
            catch (Exception ex)
            {
                var rollbackFailed = false;
                foreach (var rule in added.AsEnumerable().Reverse())
                {
                    try
                    {
                        var current = policy.ReadRules().Where(r => r.Name == rule.Name).ToArray();
                        if (current.Length == 0) continue;
                        if (current.Length == 1 && Same(current[0], rule))
                        {
                            policy.Remove(rule.Name);
                            if (policy.ReadRules().Any(r => r.Name == rule.Name)) rollbackFailed = true;
                        }
                        else rollbackFailed = true;
                    }
                    catch { rollbackFailed = true; }
                }
                return new(false, "Block failed: " + ex.Message + (rollbackFailed ? " Some newly added rules could not be rolled back; inspect owned blocks." : " Newly added rules rolled back; preexisting rules preserved.") + " Administrator permission may be required; no elevation is attempted.");
            }
        }
    }
    public NetworkResult Unblock(string id, bool explicitlyConfirmed)
    {
        if (!explicitlyConfirmed) return new(false, "Explicit confirmation is required to remove owned blocks.");
        lock (Gate)
        {
            try
            {
                var owned = Owned(policy.ReadRules());
                var rules = owned.Where(r => BlockId(r.Address, r.GamePath) == id).ToArray();
                if (rules.Length == 0) return new(false, "No exact validated owned block matches this ID.");
                foreach (var rule in rules)
                {
                    var current = policy.ReadRules().Where(r => r.Name == rule.Name).ToArray();
                    if (current.Length != 1 || !Same(current[0], rule)) throw new InvalidOperationException("Rule changed before removal; refusing deletion.");
                    policy.Remove(rule.Name);
                    if (policy.ReadRules().Any(r => r.Name == rule.Name)) throw new InvalidOperationException("Firewall rule removal readback failed.");
                }
                return new(true, "Validated app-owned UDP rules removed.");
            }
            catch (Exception ex) { return new(false, "Unblock failed: " + ex.Message + " Administrator permission may be required; inspect remaining owned blocks."); }
        }
    }
}
