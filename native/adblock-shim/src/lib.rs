//! AJBrowser's C ABI around Brave's adblock-rust engine.
//!
//! Rules for every exported function:
//! - Strings go in as UTF-8 pointer + byte length (never NUL-terminated) and come out as an [`AdbBytes`]
//!   that the caller frees with [`adb_bytes_free`].
//! - Every body runs inside `catch_unwind`, so a Rust panic becomes an error value, never an unwind into .NET.
//! - Null or invalid input is an error value, never undefined behaviour we can detect.
//! - The engine is `Send + Sync` (the "single-thread" feature is off), so any thread may call it.

use std::collections::HashSet;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::slice;

use adblock::Engine;
use adblock::lists::{FilterFormat, FilterSet, ParseOptions};
use adblock::request::Request;
use adblock::resources::{PermissionMask, Resource};

/// Bumped when the C interface changes; the C# side refuses a DLL with a different number.
const ABI_VERSION: u32 = 1;

/// A byte buffer owned by Rust. `ptr` is null when there is no value.
#[repr(C)]
pub struct AdbBytes {
    pub ptr: *mut u8,
    pub len: usize,
}

impl AdbBytes {
    fn empty() -> Self {
        AdbBytes { ptr: ptr::null_mut(), len: 0 }
    }

    fn from_vec(v: Vec<u8>) -> Self {
        let boxed = v.into_boxed_slice();
        let len = boxed.len();
        let ptr = Box::into_raw(boxed) as *mut u8;
        AdbBytes { ptr, len }
    }

    fn from_string(s: String) -> Self {
        Self::from_vec(s.into_bytes())
    }
}

/// The outcome of one network check.
#[repr(C)]
pub struct AdbCheckResult {
    /// 1 when the request should be blocked (a blocking rule matched and no exception, or `$important`).
    pub should_block: u8,
    /// 1 when a blocking rule matched (even if an exception then allowed it).
    pub matched: u8,
    /// 1 when an exception (`@@`) rule matched.
    pub exception: u8,
    /// 1 when an `$important` rule matched.
    pub important: u8,
    /// A `$redirect` replacement as a `data:` URL, or empty.
    pub redirect: AdbBytes,
    /// A `$removeparam` rewritten URL, or empty.
    pub rewritten_url: AdbBytes,
}

/// Status codes returned by functions that can fail.
pub const ADB_OK: i32 = 0;
pub const ADB_ERR_ARGUMENT: i32 = 1;
pub const ADB_ERR_PARSE: i32 = 2;
pub const ADB_ERR_PANIC: i32 = 3;

unsafe fn str_arg<'a>(p: *const u8, len: usize) -> Option<&'a str> {
    if len == 0 {
        return Some("");
    }
    if p.is_null() {
        return None;
    }
    // SAFETY: the caller guarantees `p` points at `len` readable bytes for the duration of the call.
    let bytes = unsafe { slice::from_raw_parts(p, len) };
    std::str::from_utf8(bytes).ok()
}

unsafe fn bytes_arg<'a>(p: *const u8, len: usize) -> Option<&'a [u8]> {
    if len == 0 {
        return Some(&[]);
    }
    if p.is_null() {
        return None;
    }
    // SAFETY: as above.
    Some(unsafe { slice::from_raw_parts(p, len) })
}

/// The C interface version this DLL implements.
#[unsafe(no_mangle)]
pub extern "C" fn adb_abi_version() -> u32 {
    ABI_VERSION
}

/// Frees a buffer returned by any `adb_*` function. Safe to call with an empty buffer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_bytes_free(bytes: AdbBytes) {
    if bytes.ptr.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: `ptr`/`len` came from `AdbBytes::from_vec` (a boxed slice) and are freed only once.
        unsafe {
            drop(Box::from_raw(ptr::slice_from_raw_parts_mut(bytes.ptr, bytes.len)));
        }
    }));
}

// ---------------------------------------------------------------------------------------------------------------
// Filter sets: collect lists, then turn them into an engine.
// ---------------------------------------------------------------------------------------------------------------

/// Creates an empty filter set. Free it with `adb_filter_set_free` unless it was passed to `adb_engine_build`.
#[unsafe(no_mangle)]
pub extern "C" fn adb_filter_set_new() -> *mut FilterSet {
    catch_unwind(|| Box::into_raw(Box::new(FilterSet::new(false)))).unwrap_or(ptr::null_mut())
}

/// Adds one filter list's text. `permissions` is the scriptlet trust mask for this list (0 = untrusted);
/// `hosts_format` = 1 for a hosts file instead of Adblock Plus syntax.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_filter_set_add_list(
    set: *mut FilterSet,
    text: *const u8,
    text_len: usize,
    permissions: u8,
    hosts_format: u8,
) -> i32 {
    if set.is_null() {
        return ADB_ERR_ARGUMENT;
    }
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a live filter set and a valid buffer.
        let Some(text) = (unsafe { str_arg(text, text_len) }) else {
            return ADB_ERR_ARGUMENT;
        };
        let set = unsafe { &mut *set };
        let opts = ParseOptions {
            format: if hosts_format != 0 { FilterFormat::Hosts } else { FilterFormat::Standard },
            permissions: PermissionMask::from_bits(permissions),
            ..Default::default()
        };
        set.add_filter_list(text.to_owned(), opts);
        ADB_OK
    }))
    .unwrap_or(ADB_ERR_PANIC)
}

/// Frees a filter set that was not turned into an engine.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_filter_set_free(set: *mut FilterSet) {
    if set.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: `set` came from `adb_filter_set_new` and is freed only once.
        drop(unsafe { Box::from_raw(set) });
    }));
}

// ---------------------------------------------------------------------------------------------------------------
// Engines
// ---------------------------------------------------------------------------------------------------------------

/// Builds an engine from a filter set. **Always consumes the set** (don't free it afterwards). Null on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_build(set: *mut FilterSet) -> *mut Engine {
    if set.is_null() {
        return ptr::null_mut();
    }
    // SAFETY: `set` came from `adb_filter_set_new`; ownership moves here.
    let set = unsafe { Box::from_raw(set) };
    catch_unwind(AssertUnwindSafe(move || Box::into_raw(Box::new(Engine::new_with_filter_set(*set)))))
        .unwrap_or(ptr::null_mut())
}

/// Loads an engine from bytes made by `adb_engine_serialize`. Null when the data is corrupt, truncated, or from an
/// incompatible engine version (the caller then rebuilds from the lists).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_deserialize(data: *const u8, len: usize) -> *mut Engine {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a valid buffer.
        let Some(data) = (unsafe { bytes_arg(data, len) }) else {
            return ptr::null_mut();
        };
        if data.is_empty() {
            return ptr::null_mut();
        }
        let mut engine = Engine::default();
        match engine.deserialize(data) {
            Ok(()) => Box::into_raw(Box::new(engine)),
            Err(_) => ptr::null_mut(),
        }
    }))
    .unwrap_or(ptr::null_mut())
}

/// Serializes an engine for fast loading at the next start. Empty on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_serialize(engine: *const Engine) -> AdbBytes {
    if engine.is_null() {
        return AdbBytes::empty();
    }
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a live engine.
        AdbBytes::from_vec(unsafe { &*engine }.serialize())
    }))
    .unwrap_or_else(|_| AdbBytes::empty())
}

/// Frees an engine.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_free(engine: *mut Engine) {
    if engine.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: `engine` came from `adb_engine_build`/`adb_engine_deserialize` and is freed only once.
        drop(unsafe { Box::from_raw(engine) });
    }));
}

/// Replaces the engine's resources (scriptlets for `##+js(...)` and `$redirect` bodies) with the JSON array in
/// adblock-rust's `Resource` format (as published in brave/adblock-resources `dist/resources.json`).
/// Not thread-safe against concurrent checks on the same engine: call it before the engine is shared.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_use_resources(engine: *mut Engine, json: *const u8, len: usize) -> i32 {
    if engine.is_null() {
        return ADB_ERR_ARGUMENT;
    }
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a live engine (not yet shared) and a valid buffer.
        let Some(json) = (unsafe { str_arg(json, len) }) else {
            return ADB_ERR_ARGUMENT;
        };
        let Ok(resources) = serde_json::from_str::<Vec<Resource>>(json) else {
            return ADB_ERR_PARSE;
        };
        unsafe { &mut *engine }.use_resources(resources);
        ADB_OK
    }))
    .unwrap_or(ADB_ERR_PANIC)
}

/// Checks one network request. `request_type` is one of adblock-rust's names ("document", "subdocument",
/// "script", "stylesheet", "image", "media", "font", "xmlhttprequest", "websocket", "ping", "other").
/// Returns `ADB_ERR_ARGUMENT` for an address the engine can't parse (the caller then allows the request).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_check(
    engine: *const Engine,
    url: *const u8,
    url_len: usize,
    source_url: *const u8,
    source_url_len: usize,
    request_type: *const u8,
    request_type_len: usize,
    out: *mut AdbCheckResult,
) -> i32 {
    if engine.is_null() || out.is_null() {
        return ADB_ERR_ARGUMENT;
    }
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a live engine, valid buffers and a writable result.
        let (Some(url), Some(source), Some(kind)) = (unsafe { str_arg(url, url_len) }, unsafe {
            str_arg(source_url, source_url_len)
        }, unsafe { str_arg(request_type, request_type_len) }) else {
            return ADB_ERR_ARGUMENT;
        };
        let Ok(request) = Request::new(url, source, kind, "get") else {
            return ADB_ERR_ARGUMENT;
        };
        let result = unsafe { &*engine }.check_network_request(&request);
        let value = AdbCheckResult {
            should_block: result.should_block() as u8,
            matched: result.filter.is_some() as u8,
            exception: result.exception.is_some() as u8,
            important: result.important as u8,
            redirect: result.redirect.map(AdbBytes::from_string).unwrap_or_else(AdbBytes::empty),
            rewritten_url: result.rewritten_url.map(AdbBytes::from_string).unwrap_or_else(AdbBytes::empty),
        };
        unsafe { out.write(value) };
        ADB_OK
    }))
    .unwrap_or(ADB_ERR_PANIC)
}

/// The cosmetic resources for a page, as JSON:
/// `{"hide_selectors":[…],"procedural_actions":[…],"exceptions":[…],"injected_script":"…","generichide":false}`.
/// Empty on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_url_cosmetic_resources(engine: *const Engine, url: *const u8, len: usize) -> AdbBytes {
    if engine.is_null() {
        return AdbBytes::empty();
    }
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a live engine and a valid buffer.
        let Some(url) = (unsafe { str_arg(url, len) }) else {
            return AdbBytes::empty();
        };
        let resources = unsafe { &*engine }.url_cosmetic_resources(url);
        serde_json::to_vec(&resources).map(AdbBytes::from_vec).unwrap_or_else(|_| AdbBytes::empty())
    }))
    .unwrap_or_else(|_| AdbBytes::empty())
}

#[derive(serde::Deserialize)]
struct ClassIdQuery {
    #[serde(default)]
    classes: Vec<String>,
    #[serde(default)]
    ids: Vec<String>,
    #[serde(default)]
    exceptions: HashSet<String>,
}

/// Generic hide selectors for class names and ids seen on a page. Input JSON:
/// `{"classes":[…],"ids":[…],"exceptions":[…]}` (exceptions from the page's cosmetic resources).
/// Output: a JSON array of selectors. Empty on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn adb_engine_hidden_class_id_selectors(
    engine: *const Engine,
    json: *const u8,
    len: usize,
) -> AdbBytes {
    if engine.is_null() {
        return AdbBytes::empty();
    }
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller passes a live engine and a valid buffer.
        let Some(json) = (unsafe { str_arg(json, len) }) else {
            return AdbBytes::empty();
        };
        let Ok(query) = serde_json::from_str::<ClassIdQuery>(json) else {
            return AdbBytes::empty();
        };
        let selectors = unsafe { &*engine }.hidden_class_id_selectors(&query.classes, &query.ids, &query.exceptions);
        serde_json::to_vec(&selectors).map(AdbBytes::from_vec).unwrap_or_else(|_| AdbBytes::empty())
    }))
    .unwrap_or_else(|_| AdbBytes::empty())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn engine(list: &str) -> *mut Engine {
        let set = adb_filter_set_new();
        unsafe {
            assert_eq!(adb_filter_set_add_list(set, list.as_ptr(), list.len(), 0, 0), ADB_OK);
            adb_engine_build(set)
        }
    }

    fn check(e: *const Engine, url: &str, source: &str, kind: &str) -> AdbCheckResult {
        let mut out = std::mem::MaybeUninit::<AdbCheckResult>::uninit();
        let rc = unsafe {
            adb_engine_check(e, url.as_ptr(), url.len(), source.as_ptr(), source.len(), kind.as_ptr(), kind.len(), out.as_mut_ptr())
        };
        assert_eq!(rc, ADB_OK);
        unsafe { out.assume_init() }
    }

    #[test]
    fn blocks_and_excepts() {
        let e = engine("||ads.example.com^\n@@||ads.example.com/ok.js\n");
        let r = check(e, "https://ads.example.com/a.js", "https://news.test/", "script");
        assert_eq!(r.should_block, 1);
        let r = check(e, "https://ads.example.com/ok.js", "https://news.test/", "script");
        assert_eq!((r.should_block, r.matched, r.exception), (0, 1, 1));
        unsafe { adb_engine_free(e) };
    }

    #[test]
    fn serialization_round_trips() {
        let e = engine("||tracker.test^\n");
        let bytes = unsafe { adb_engine_serialize(e) };
        let e2 = unsafe { adb_engine_deserialize(bytes.ptr, bytes.len) };
        assert!(!e2.is_null());
        assert_eq!(check(e2, "https://tracker.test/p", "https://a.test/", "image").should_block, 1);
        unsafe {
            adb_bytes_free(bytes);
            adb_engine_free(e);
            adb_engine_free(e2);
        }
    }

    #[test]
    fn rejects_garbage_engine_bytes() {
        let junk = b"not an engine";
        assert!(unsafe { adb_engine_deserialize(junk.as_ptr(), junk.len()) }.is_null());
    }
}
