package com.droidlens.app.network

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import com.droidlens.app.logD
import com.droidlens.app.logE

class NsdHelper(context: Context) {

    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var registrationListener: NsdManager.RegistrationListener? = null

    fun register(port: Int) {
        val serviceInfo = NsdServiceInfo().apply {
            serviceName = "DroidLens"
            serviceType = "_DroidLens._tcp."
            setPort(port)
            // Advertised so the PC client can identify/key auto-connect on the
            // phone's model before ever opening a TCP connection — serviceName
            // above is a fixed constant ("DroidLens"), not device-specific, so
            // it can't be used for that. See MainViewModel.cs's
            // DeviceAutoConnectKey / MdnsDiscovery.cs's TXT-record parsing.
            setAttribute("model", DeviceModelResolver.resolve())
        }

        registrationListener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {
                logD("DroidLens") { "mDNS registered: ${info.serviceName}" }
            }
            override fun onRegistrationFailed(info: NsdServiceInfo, code: Int) {
                logE("DroidLens") { "mDNS registration failed: $code" }
            }
            override fun onServiceUnregistered(info: NsdServiceInfo) {
                logD("DroidLens") { "mDNS unregistered" }
            }
            override fun onUnregistrationFailed(info: NsdServiceInfo, code: Int) {
                logE("DroidLens") { "mDNS unregister failed: $code" }
            }
        }

        nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, registrationListener!!)
    }

    fun unregister() {
        registrationListener?.let {
            runCatching { nsdManager.unregisterService(it) }
            registrationListener = null
        }
    }
}