package com.tclcontrol.app

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothProfile
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper

@SuppressLint("MissingPermission")
class TclSoundbarBle(private val context: Context) {

    interface Listener {
        fun onConnectionState(connected: Boolean)
        fun onStatus(status: TclProtocol.Incoming.Status)
        fun onLog(line: String)
    }

    private var gatt: BluetoothGatt? = null
    private var writeChar: BluetoothGattCharacteristic? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    var listener: Listener? = null

    private var pendingDevice: BluetoothDevice? = null
    private var retriesLeft = 0

    fun connect(device: BluetoothDevice) {
        // Always release any previous GATT client first - each connectGatt() call registers a new
        // client interface (limited pool in the BT stack) and leaking them causes every later
        // connect attempt to fail at the radio level with GATT_Status 255, even for a fresh device.
        gatt?.close()
        gatt = null
        pendingDevice = device
        retriesLeft = 2
        doConnect(device)
    }

    private fun doConnect(device: BluetoothDevice) {
        // Explicit TRANSPORT_LE: this device also has a classic A2DP profile bonded under the
        // same address (for audio streaming), so default/auto transport selection can pick BR/EDR
        // and fail to find the GATT service that only exists over the LE link.
        gatt = device.connectGatt(context, false, gattCallback, BluetoothDevice.TRANSPORT_LE)
    }

    fun disconnect() {
        pendingDevice = null
        gatt?.disconnect()
        gatt?.close()
        gatt = null
        writeChar = null
    }

    fun sendCommand(attrId: Int, value: Int) {
        val char = writeChar ?: run {
            listener?.onLog("not connected, cannot send attr=$attrId")
            return
        }
        val frame = TclProtocol.buildSetCommand(attrId, value)
        char.writeType = BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            gatt?.writeCharacteristic(char, frame, BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE)
        } else {
            @Suppress("DEPRECATION")
            char.value = frame
            @Suppress("DEPRECATION")
            gatt?.writeCharacteristic(char)
        }
        listener?.onLog("TX ${TclProtocol.attrName(attrId)}=$value  ${frame.joinToString(" ") { "%02x".format(it) }}")
    }

    fun refreshStatus() = sendCommand(TclProtocol.Attr.GET_STATUS, 0)

    private val gattCallback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            when (newState) {
                BluetoothProfile.STATE_CONNECTED -> {
                    retriesLeft = 0
                    mainHandler.post { listener?.onLog("connected, discovering services...") }
                    g.discoverServices()
                }
                BluetoothProfile.STATE_DISCONNECTED -> {
                    writeChar = null
                    g.close()
                    gatt = null
                    val device = pendingDevice
                    if (status != android.bluetooth.BluetoothGatt.GATT_SUCCESS && device != null && retriesLeft > 0) {
                        retriesLeft -= 1
                        mainHandler.post { listener?.onLog("connect failed (status=$status), retrying...") }
                        mainHandler.postDelayed({ doConnect(device) }, 600)
                    } else {
                        pendingDevice = null
                        mainHandler.post {
                            listener?.onConnectionState(false)
                            listener?.onLog("disconnected (status=$status)")
                        }
                    }
                }
            }
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            val service = g.getService(TclProtocol.SERVICE_UUID)
            if (service == null) {
                mainHandler.post { listener?.onLog("service ${TclProtocol.SERVICE_UUID} not found") }
                return
            }
            writeChar = service.getCharacteristic(TclProtocol.WRITE_CHAR_UUID)
            val notifyChar = service.getCharacteristic(TclProtocol.NOTIFY_CHAR_UUID)
            if (notifyChar != null) {
                g.setCharacteristicNotification(notifyChar, true)
                val cccd = notifyChar.getDescriptor(TclProtocol.CCCD_UUID)
                if (cccd != null) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        g.writeDescriptor(cccd, BluetoothGattDescriptorCompatValue.ENABLE_NOTIFICATION_VALUE)
                    } else {
                        @Suppress("DEPRECATION")
                        cccd.value = BluetoothGattDescriptorCompatValue.ENABLE_NOTIFICATION_VALUE
                        @Suppress("DEPRECATION")
                        g.writeDescriptor(cccd)
                    }
                }
            }
            mainHandler.post {
                listener?.onConnectionState(true)
                listener?.onLog("ready (write=${writeChar != null} notify=${notifyChar != null})")
            }
        }

        override fun onCharacteristicChanged(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray
        ) {
            handleIncoming(value)
        }

        @Suppress("DEPRECATION")
        override fun onCharacteristicChanged(g: BluetoothGatt, characteristic: BluetoothGattCharacteristic) {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
                handleIncoming(characteristic.value ?: return)
            }
        }

        private fun handleIncoming(value: ByteArray) {
            val parsed = TclProtocol.parseIncoming(value)
            mainHandler.post {
                when (parsed) {
                    is TclProtocol.Incoming.Status -> listener?.onStatus(parsed)
                    is TclProtocol.Incoming.Ack -> { /* generic ack, ignore */ }
                    else -> listener?.onLog("RX ${value.joinToString(" ") { "%02x".format(it) }}")
                }
            }
        }
    }
}

private object BluetoothGattDescriptorCompatValue {
    val ENABLE_NOTIFICATION_VALUE: ByteArray = byteArrayOf(0x01, 0x00)
}
