LOCAL_PATH := $(call my-dir)
include $(CLEAR_VARS)
LOCAL_MODULE := tvsaudio
LOCAL_SRC_FILES := aaudio_out.c
LOCAL_LDLIBS := -laaudio
# API 28 calls (usage, content type) are guarded by __builtin_available.
LOCAL_CFLAGS := -D__ANDROID_UNAVAILABLE_SYMBOLS_ARE_WEAK__ -Werror=unguarded-availability
include $(BUILD_SHARED_LIBRARY)
