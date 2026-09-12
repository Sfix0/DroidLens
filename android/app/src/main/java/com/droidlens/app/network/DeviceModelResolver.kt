package com.droidlens.app.network

import android.os.Build

/**
 * Resolves the phone's marketing/display name — e.g. "Samsung Galaxy S21"
 * instead of the technical codename Build.MODEL usually holds (e.g. "SM-G991B").
 *
 * Shared by CommandServer (sent as JSON device_info to a connected client)
 * and NsdHelper (advertised as an mDNS TXT record attribute, so it's known
 * even before any TCP connection is made — see MainViewModel.cs's
 * auto-connect key, which is keyed on this same model string).
 */
object DeviceModelResolver {
    fun resolve(): String {
        val marketingName = try {
            val c = Class.forName("android.os.SystemProperties")
            val m = c.getMethod("get", String::class.java)
            m.invoke(null, "ro.product.marketname") as? String
        } catch (_: Exception) { null }

        return when {
            !marketingName.isNullOrBlank() -> marketingName
            else -> "${Build.MANUFACTURER} ${Build.MODEL}"
        }
    }
}