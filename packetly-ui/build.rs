fn main() {
    // embed the app icon into the exe (MSVC uses rc.exe; the local GNU toolchain
    // has no windres, so icon embedding only runs in MSVC/CI builds)
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        // rc.exe compiles the .rc from OUT_DIR, so the icon must be an ABSOLUTE path
        let manifest = std::env::var("CARGO_MANIFEST_DIR").unwrap_or_default();
        let icon = std::path::Path::new(&manifest)
            .join("..")
            .join("..")
            .join("assets")
            .join("packetly.ico")
            .canonicalize()
            .unwrap_or_else(|_| {
                std::path::Path::new(&manifest)
                    .join("..")
                    .join("..")
                    .join("assets")
                    .join("packetly.ico")
            });
        if icon.exists() {
            let mut res = winresource::WindowsResource::new();
            res.set_icon(icon.to_str().unwrap());
            if let Err(e) = res.compile() {
                // never break the release over the exe-file icon: the taskbar/window
                // icon is set at runtime from the embedded PNG regardless
                println!("cargo:warning=icon embed failed: {e}");
            }
        } else {
            println!(
                "cargo:warning=packetly.ico not found at {}, skipping exe icon embed",
                icon.display()
            );
        }
    }
}
