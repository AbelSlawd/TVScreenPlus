// Template of the TV-home shortcuts: a tiny app that opens TVScreenPlus. Built unsigned into the TV app's assets,
// then rewritten (package, name, icons) and signed on the TV for each shortcut: see app/.../Shortcuts.kt.
plugins {
    id("com.android.application")
}

android {
    namespace = "com.tvscreenplus.shortcut"
    compileSdk = 35
    defaultConfig {
        // Placeholder, replaced on the TV by "<creator package>.sc.x<random hex>" of the same length (52).
        applicationId = "com.tvscreenplus.sc.x" + "a".repeat(31)
        minSdk = 24 // APK signature v2 only (signed on the TV)
        // Recent target, else Play Protect blocks the install ("made for an older Android"); so resources.arsc is
        // stored uncompressed in the rewritten zip (apksig aligns it).
        targetSdk = 35
        versionCode = 1
        versionName = "1"
    }
    buildTypes { release { isMinifyEnabled = false } }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
