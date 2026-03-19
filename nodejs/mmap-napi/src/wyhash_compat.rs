// NOTE: This is a compatibility checksum used by TigaIpc/mmap-sync.
// Do NOT replace with the `wyhash` crate (or any other variant), or
// cross-language interoperability will break.

const WYHASH_SECRET0: u64 = 0xa0761d6478bd642f;
const WYHASH_SECRET1: u64 = 0xe7037ed1a0b428db;
const WYHASH_SECRET2: u64 = 0x8ebc6af09c88c6e3;
const WYHASH_SECRET3: u64 = 0x589965cc75374cc3;

fn wyhash_mix(a: u64, b: u64) -> u64 {
    let product = (a as u128).wrapping_mul(b as u128);
    let low = product as u64;
    let high = (product >> 64) as u64;
    high ^ low
}

fn read_u64_le(data: &[u8], offset: usize) -> u64 {
    let mut buf = [0u8; 8];
    buf.copy_from_slice(&data[offset..offset + 8]);
    u64::from_le_bytes(buf)
}

fn read_u32_le(data: &[u8], offset: usize) -> u32 {
    let mut buf = [0u8; 4];
    buf.copy_from_slice(&data[offset..offset + 4]);
    u32::from_le_bytes(buf)
}

fn read3(data: &[u8]) -> u64 {
    let len = data.len();
    ((data[0] as u64) << 16) | ((data[len >> 1] as u64) << 8) | (data[len - 1] as u64)
}

pub fn wyhash_hash_compat(data: &[u8]) -> u64 {
    let mut seed = WYHASH_SECRET0; // seed = 0 ^ secret0
    let len = data.len() as u64;
    let a;
    let b;

    if len <= 16 {
        if len >= 4 {
            let shift = ((len >> 3) << 2) as usize;
            a = ((read_u32_le(data, 0) as u64) << 32) | (read_u32_le(data, shift) as u64);
            let tail = data.len() - 4;
            b = ((read_u32_le(data, tail) as u64) << 32) | (read_u32_le(data, tail - shift) as u64);
        } else if len > 0 {
            a = read3(data);
            b = 0;
        } else {
            a = 0;
            b = 0;
        }
    } else {
        let mut i = len;
        let mut p = 0usize;
        if i > 48 {
            let mut seed1 = seed;
            let mut seed2 = seed;
            while i > 48 {
                seed = wyhash_mix(
                    read_u64_le(data, p) ^ WYHASH_SECRET1,
                    read_u64_le(data, p + 8) ^ seed,
                );
                seed1 = wyhash_mix(
                    read_u64_le(data, p + 16) ^ WYHASH_SECRET2,
                    read_u64_le(data, p + 24) ^ seed1,
                );
                seed2 = wyhash_mix(
                    read_u64_le(data, p + 32) ^ WYHASH_SECRET3,
                    read_u64_le(data, p + 40) ^ seed2,
                );
                p += 48;
                i -= 48;
            }
            seed ^= seed1 ^ seed2;
        }

        while i > 16 {
            seed = wyhash_mix(
                read_u64_le(data, p) ^ WYHASH_SECRET1,
                read_u64_le(data, p + 8) ^ seed,
            );
            p += 16;
            i -= 16;
        }

        let end = p + i as usize;
        a = read_u64_le(data, end - 16);
        b = read_u64_le(data, end - 8);
    }

    wyhash_mix(
        WYHASH_SECRET1 ^ len,
        wyhash_mix(a ^ WYHASH_SECRET1, b ^ seed),
    )
}

pub fn compute_checksum_compat(data: &[u8], bits: usize) -> u64 {
    wyhash_hash_compat(data) & ((1u64 << bits) - 1)
}
