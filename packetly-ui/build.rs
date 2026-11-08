fn main() {
    // embed the app icon into the exe (MSVC uses rc.exe; the local GNU toolchain
    // has no windres, so icon embedding only runs in MSVC/CI builds)
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        winresource::WindowsResource::new()
            .set_icon("../../assets/packetly.ico")
            .compile()
            .expect("failed to embed icon");
    }
}
