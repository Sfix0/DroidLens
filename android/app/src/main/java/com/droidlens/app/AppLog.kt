package com.droidlens.app

import android.util.Log

inline fun logD(tag: String, msg: () -> String) {
    if (BuildConfig.DEBUG) Log.d(tag, msg())
}

@Suppress("unused")
inline fun logW(tag: String, msg: () -> String) {
    if (BuildConfig.DEBUG) Log.w(tag, msg())
}

inline fun logE(tag: String, msg: () -> String) {
    if (BuildConfig.DEBUG) Log.e(tag, msg())
}