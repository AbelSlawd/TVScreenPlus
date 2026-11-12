plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.tvscreenplus"
    compileSdk = 35
    ndkVersion = "27.2.12479018"
    externalNativeBuild { ndkBuild { path = file("src/main/jni/Android.mk") } }
    defaultConfig {
        applicationId = "com.tvscreenplus"
        minSdk = 21
        targetSdk = 35
        val build = (project.findProperty("buildNumber") as String?)?.toIntOrNull() ?: 1
        versionCode = build
        // Shown to people: the VERSION file (CI passes it); versionCode = CI run, compared by the updater.
        versionName = (project.findProperty("versionName") as String?) ?: "1.1.$build"
        // AAudio needs API 26; the library is only loaded there (minSdk stays 21 for the Java side).
        externalNativeBuild { ndkBuild { arguments("APP_PLATFORM=android-26") } }
        ndk { abiFilters += listOf("armeabi-v7a", "arm64-v8a") } // TVs are ARM
    }
    // Fixed key (from CI secrets) so every release installs over the previous one; debug key otherwise.
    val ksFile = System.getenv("ANDROID_KEYSTORE_FILE")?.let { file(it) }?.takeIf { it.exists() }
    signingConfigs {
        create("release") {
            if (ksFile != null) {
                storeFile = ksFile
                storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ANDROID_KEY_ALIAS") ?: "tvsp"
                keyPassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
            }
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            signingConfig = signingConfigs.getByName(if (ksFile != null) "release" else "debug")
            // -Pdev: local test build (debug key) installed next to the released app instead of over it.
            if (project.hasProperty("dev")) applicationIdSuffix = ".dev"
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}
