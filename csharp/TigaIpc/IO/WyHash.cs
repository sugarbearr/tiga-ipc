using System.Runtime.CompilerServices;

namespace TigaIpc.IO;

internal static class WyHash
{
    private const ulong Secret0 = 0xa0761d6478bd642f;
    private const ulong Secret1 = 0xe7037ed1a0b428db;
    private const ulong Secret2 = 0x8ebc6af09c88c6e3;
    private const ulong Secret3 = 0x589965cc75374cc3;

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* ptr = data)
            {
                return Hash(ptr, data.Length);
            }
        }
    }

    public static unsafe ulong Hash(byte* data, long length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ulong seed = 0 ^ Secret0;
        ulong len = (ulong)length;
        ulong a;
        ulong b;
        byte* p = data;

        if (len <= 16)
        {
            if (len >= 4)
            {
                var shift = (int)((len >> 3) << 2);
                a = ((ulong)Read32(p) << 32) | Read32(p + shift);
                b = ((ulong)Read32(p + (long)len - 4) << 32) | Read32(p + (long)len - 4 - shift);
            }
            else if (len > 0)
            {
                a = Read3(p, (int)len);
                b = 0;
            }
            else
            {
                a = 0;
                b = 0;
            }
        }
        else
        {
            ulong i = len;
            if (i > 48)
            {
                ulong seed1 = seed;
                ulong seed2 = seed;
                do
                {
                    seed = Wymix(Read64(p) ^ Secret1, Read64(p + 8) ^ seed);
                    seed1 = Wymix(Read64(p + 16) ^ Secret2, Read64(p + 24) ^ seed1);
                    seed2 = Wymix(Read64(p + 32) ^ Secret3, Read64(p + 40) ^ seed2);
                    p += 48;
                    i -= 48;
                } while (i > 48);
                seed ^= seed1 ^ seed2;
            }

            while (i > 16)
            {
                seed = Wymix(Read64(p) ^ Secret1, Read64(p + 8) ^ seed);
                p += 16;
                i -= 16;
            }

            a = Read64(p + (long)i - 16);
            b = Read64(p + (long)i - 8);
        }

        return Wymix(Secret1 ^ len, Wymix(a ^ Secret1, b ^ seed));
    }

    private static ulong Wymix(ulong a, ulong b)
    {
        Multiply64(a, b, out var high, out var low);
        return high ^ low;
    }

    private static unsafe ulong Read64(byte* data)
    {
        return Unsafe.ReadUnaligned<ulong>(data);
    }

    private static unsafe uint Read32(byte* data)
    {
        return Unsafe.ReadUnaligned<uint>(data);
    }

    private static unsafe ulong Read3(byte* data, int length)
    {
        return ((ulong)data[0] << 16) | ((ulong)data[length >> 1] << 8) | data[length - 1];
    }

    private static void Multiply64(ulong x, ulong y, out ulong high, out ulong low)
    {
        ulong x0 = (uint)x;
        ulong x1 = x >> 32;
        ulong y0 = (uint)y;
        ulong y1 = y >> 32;

        ulong p11 = x1 * y1;
        ulong p01 = x0 * y1;
        ulong p10 = x1 * y0;
        ulong p00 = x0 * y0;

        ulong middle = (p10 & 0xffffffff) + (p01 & 0xffffffff) + (p00 >> 32);
        high = p11 + (p10 >> 32) + (p01 >> 32) + (middle >> 32);
        low = (middle << 32) | (p00 & 0xffffffff);
    }
}
