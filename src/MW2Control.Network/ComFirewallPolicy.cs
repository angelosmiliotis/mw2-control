using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MW2Control.Network;

/// <summary>No COM activation occurs in the constructor; no shell or profile mutation is used.</summary>
internal sealed class ComFirewallPolicy : IFirewallPolicy
{
    public IReadOnlyList<FirewallRule> ReadRules()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Firewall is required.");
        return WithRules(rules =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var result = new List<FirewallRule>();
            foreach (var item in (IEnumerable)rules)
            {
                try
                {
                    dynamic rule = item;
                    string name = rule.Name;
                    string group = rule.Grouping ?? "";
                    if (!name.StartsWith(FirewallBlockService.Prefix, StringComparison.Ordinal) && group != FirewallBlockService.Group) continue;
                    string address = rule.RemoteAddresses;
                    // COM may render an exact host as a full-length mask. Never accept broader masks.
                    if (address.EndsWith("/255.255.255.255", StringComparison.Ordinal)) address = address[..^16];
                    else if (address.EndsWith("/128", StringComparison.Ordinal)) address = address[..^4];
                    result.Add(new(name, group, address, (string)rule.ApplicationName, (int)rule.Direction,
                        (int)rule.Protocol, (int)rule.Action, (bool)rule.Enabled, (int)rule.Profiles,
                        (string)rule.LocalAddresses, (string)rule.LocalPorts, (string)rule.RemotePorts,
                        (string)rule.InterfaceTypes, (string?)rule.ServiceName ?? "", (bool)rule.EdgeTraversal));
                }
                finally { Release(item); }
            }
            return (IReadOnlyList<FirewallRule>)result;
        });
    }
    public void Add(FirewallRule value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Firewall is required.");
        WithRules<object?>(rules =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            try
            {
                rule.Name = value.Name; rule.Grouping = value.Group;
                rule.Description = "MW2 CONTROL exact peer UDP block. User confirmed; removal through owned block list.";
                rule.ApplicationName = value.GamePath; rule.Protocol = value.Protocol;
                rule.LocalAddresses = value.LocalAddresses; rule.RemoteAddresses = value.Address;
                rule.LocalPorts = value.LocalPorts; rule.RemotePorts = value.RemotePorts;
                rule.Direction = value.Direction; rule.Action = value.Action; rule.Profiles = value.Profiles;
                rule.InterfaceTypes = value.InterfaceTypes; rule.EdgeTraversal = false; rule.Enabled = true;
                rules.Add(rule);
                return null;
            }
            finally { Release(rule); }
        });
    }
    public void Remove(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Firewall is required.");
        WithRules<object?>(rules => { rules.Remove(name); return null; });
    }
    [SupportedOSPlatform("windows")]
    private static T WithRules<T>(Func<dynamic, T> operation)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
        object? rules = null;
        try { rules = policy.Rules; return operation(rules); }
        finally { Release(rules); Release(policy); }
    }
    [SupportedOSPlatform("windows")]
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
