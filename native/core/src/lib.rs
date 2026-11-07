#![no_std]
// Bounded WASM ABI, called in-process by Go on all desktop platforms.
use core::{cell::UnsafeCell, panic::PanicInfo};
const CAPACITY: usize = 4 * 1024 * 1024 + 4096;
struct Buffer(UnsafeCell<[u8; CAPACITY]>);
unsafe impl Sync for Buffer {}
static BUFFER: Buffer = Buffer(UnsafeCell::new([0; CAPACITY]));
#[panic_handler]
fn panic(_: &PanicInfo) -> ! { loop {} }
#[no_mangle]
pub extern "C" fn buffer() -> u32 { BUFFER.0.get() as *mut u8 as u32 }
#[no_mangle]
pub extern "C" fn capacity() -> u32 { CAPACITY as u32 }
#[no_mangle]
pub extern "C" fn abi_version() -> u32 { 1 }

// Returns first match at/after start; -1 = absent, -2 = invalid bounds.
#[no_mangle]
pub extern "C" fn find(data_len: u32, needle_len: u32, start: u32) -> i32 {
    let (d, n, s) = (data_len as usize, needle_len as usize, start as usize);
    if n == 0 || n > 4096 || d > CAPACITY - n || s > d { return -2; }
    if n > d || s > d - n { return -1; }
    // Go serializes calls and writes only within this fixed module memory.
    let all = unsafe { &*BUFFER.0.get() };
    let data = &all[..d]; let needle = &all[d..d+n];
    let mut skip = [n; 256];
    for i in 0..n-1 { skip[needle[i] as usize] = n - i - 1; }
    let mut offset = s;
    while offset <= d - n {
        if data[offset..offset+n] == *needle { return offset as i32; }
        offset += skip[data[offset+n-1] as usize];
    }
    -1
}
