"""Read-only, bounded offline registration evidence for the one approved executable.

This does not load/execute the PE or open a process. Only stdlib is used. Output
is byte evidence, not a general-purpose disassembler or a first-match locator.
"""
import hashlib
import pathlib
import struct
import sys

EXPECTED = "926F8A47959F2B24420898D07F599883E0866050D42D5289F94EEF546A4A3492"
data = pathlib.Path(sys.argv[1]).read_bytes()
assert len(data) <= 32 * 1024 * 1024
assert hashlib.sha256(data).hexdigest().upper() == EXPECTED, "Unsupported executable"
pe = struct.unpack_from("<I", data, 0x3C)[0]
assert data[pe:pe + 4] == b"PE\0\0"
machine, count = struct.unpack_from("<HH", data, pe + 4)
optional_size = struct.unpack_from("<H", data, pe + 20)[0]
assert machine == 0x8664 and count < 64
sections = []
for i in range(count):
    at = pe + 24 + optional_size + i * 40
    name = data[at:at + 8].rstrip(b"\0").decode("ascii")
    virtual_size, rva, raw_size, offset = struct.unpack_from("<IIII", data, at + 8)
    characteristics = struct.unpack_from("<I", data, at + 36)[0]
    sections.append((name, rva, virtual_size, offset, raw_size, characteristics))

def rva_for_offset(offset):
    return next(rva + offset - start for _, rva, _, start, size, _ in sections if start <= offset < start + size)

def all_offsets(pattern):
    start = 0
    while (found := data.find(pattern, start)) >= 0:
        yield found
        start = found + 1

print("SHA256", EXPECTED)
for name in ["cg_fov", "com_maxfps", "snd_enableStream", "r_fullbright", "r_fog", "r_glow", "r_specularMap", "r_detail", "r_detailMap", "cg_scoreboardPingText", "cg_scoreboardPingGraph"]:
    strings = list(all_offsets(name.encode() + b"\0"))
    print("NAME", name, "string RVAs", [hex(rva_for_offset(s)) for s in strings])
    targets = {rva_for_offset(s) for s in strings}
    for section, base, _, offset, size, flags in sections:
        if not flags & 0x20000000:
            continue
        for at in range(offset, offset + size - 7):
            # RIP-relative LEA rcx is the x64 name argument at registration sites.
            if data[at:at + 3] != b"\x48\x8d\x0d":
                continue
            target = base + at - offset + 7 + struct.unpack_from("<i", data, at + 3)[0]
            if target not in targets:
                continue
            site = base + at - offset
            print(" LEA name", hex(site))
            lo, hi = max(offset, at - 48), min(offset + size, at + 96)
            print(" bytes", hex(base + lo - offset), data[lo:hi].hex(" "))
            for following in range(at + 7, hi - 7):
                pos = base + following - offset
                if data[following] == 0xE8:
                    print(" call candidate", hex(pos), "->", hex(pos + 5 + struct.unpack_from("<i", data, following + 1)[0]))
                if data[following:following + 3] == b"\x48\x89\x05":
                    print(" store rax", hex(pos), "-> slot", hex(pos + 7 + struct.unpack_from("<i", data, following + 3)[0]))

for argument in sys.argv[2:]:
    requested, length = (int(part, 0) for part in argument.split(":"))
    assert 0 < length <= 1024
    section = next(s for s in sections if s[1] <= requested and requested + length <= s[1] + s[4])
    offset = section[3] + requested - section[1]
    for row in range(0, length, 16):
        print(hex(requested + row), data[offset + row:offset + min(length, row + 16)].hex(" "))
