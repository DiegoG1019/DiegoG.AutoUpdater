using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DiegoG.AutoUpdater;

public readonly record struct VersionHash(ulong A, ulong B, ulong C, ulong D, ulong E, ulong F, ulong G, ulong H)
{
    public unsafe static VersionHash Create(Version version)
    {
        var r = new VersionHash();
        Span<byte> bytes = new(&r, sizeof(VersionHash));
        Span<int> v = stackalloc int[4];
        v[0] = version.Major;
        v[1] = version.Minor; 
        v[2] = version.Revision;
        v[3] = version.Build;

        SHA512.HashData(MemoryMarshal.AsBytes(v), bytes);

        return r;
    }

    public unsafe static VersionHash Create(string str)
    {
        var r = new VersionHash();
        Span<byte> bytes = new(&r, sizeof(VersionHash));
        Span<byte> chars = stackalloc byte[Encoding.UTF8.GetByteCount(str)];
        Encoding.UTF8.GetBytes(str, chars);

        SHA512.HashData(chars, bytes); 
        return r;
    }

    public unsafe static VersionHash LoadFrom(Stream source)
    {
        var r = new VersionHash();
        Span<byte> bytes = new(&r, sizeof(VersionHash));
        return source.Read(bytes) < sizeof(VersionHash)
            ? throw new InvalidDataException("source stream did not have enough remaining bytes to fully create a new VersionHash")
            : r;
    }

    public unsafe int CopyTo(Span<byte> destination)
    {
        int written = 0;
        fixed (VersionHash* self = &this)
        {
            ReadOnlySpan<byte> bytes = new(self, sizeof(VersionHash));
            for (; written < destination.Length && written < (sizeof(VersionHash)); written++)
                destination[written] = bytes[written];
        }
        return written;
    }

    public unsafe void CopyTo(Stream destination)
    {
        fixed (VersionHash* self = &this)
        {
            ReadOnlySpan<byte> bytes = new(self, sizeof(VersionHash));
            destination.Write(bytes);
        }
    }

    public unsafe override string ToString()
    {
        fixed (VersionHash* self = &this)
        {
            ReadOnlySpan<byte> bytes = new(self, sizeof(VersionHash));
            return Convert.ToHexString(bytes);
        }
    }
}