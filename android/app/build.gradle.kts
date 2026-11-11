plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.tvscreenplus"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.tvscreenplus"
        minSdk = 21
        targetSdk = 35
        val build = (project.findProperty("buildNumber") as String?)?.toIntOrNull() ?: 1
        versionCode = build
        versionName = "1.0.$build"
    }
    // Fixed key (from CI secrets) so every release installs over the previous one; debug key otherwise.
    val ksFile = System.getenv("ANDROID_KEYSTORE_FILE")
    signingConfigs {
        create("release") {
            if (ksFile != null && file(ksFile).exists()) {
                storeFile = file(ksFile)
                storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ANDROID_KEY_ALIAS") ?: "tvsp"
                keyPassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
            } else initWith(getByName("debug"))
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            signingConfig = signingConfigs.getByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}
