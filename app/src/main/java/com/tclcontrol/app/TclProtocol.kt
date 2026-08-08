package com.tclcontrol.app

import java.util.UUID

/**
 * Reverse-engineered from a btsnoop capture of TCL Home <-> TCL S45H soundbar.
 * Frame: 52 43 (magic "RC") | type(1) | seq(u16 LE) | counter(u16 LE) | 06 00 | attrId(u16 LE) | value(u16 LE, low byte signed) | 00
 * Status notifications echo the same shape with attrId = setAttrId + STATUS_OFFSET.
 */
object TclProtocol {
    val SERVICE_UUID: UUID = UUID.fromString("0000f500-0000-1000-8000-00805f9b34fb")
    val WRITE_CHAR_UUID: UUID = UUID.fromString("e49a25e0-f69a-11e8-8eb2-f2801f1b9fd1")
    val NOTIFY_CHAR_UUID: UUID = UUID.fromString("e49a28e1-f69a-11e8-8eb2-f2801f1b9fd1")
    val CCCD_UUID: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

    private const val MAGIC_HI: Byte = 0x52
    private const val MAGIC_LO: Byte = 0x43
    private const val TYPE_REQUEST: Byte = 0x01
    private const val TYPE_RESPONSE: Byte = 0x02
    private const val REQ_SEQ = 1
    private const val STATUS_OFFSET = 0x82

    object Attr {
        const val GET_STATUS = 0x01
        const val VOLUME = 0x02
        const val BASS = 0x04
        const val TREBLE = 0x05
        const val POWER_QUERY = 0x06
        const val MUTE = 0x0e
        const val SOURCE = 0x0f
        const val SOUND_MODE = 0x11
        const val ATMOS_DTS = 0x12
        const val GET_DEVICE_INFO = 0x18
    }

    /**
     * The "counter" field is not a sequence number - it's a checksum: the low byte of the sum of the
     * 7 inner-payload bytes (06 00 attrIdLo attrIdHi valueLo valueHi 00). Confirmed by replaying a live
     * capture: identical payloads always produced the identical counter, and the value tracked
     * (0x06 + attrId + value) mod 256 exactly. The device NACKs (ack payload "01 ff" instead of "01 aa")
     * any frame whose counter doesn't match this checksum.
     */
    private fun checksum(inner: ByteArray): Int {
        var sum = 0
        for (b in inner) sum += (b.toInt() and 0xFF)
        return sum and 0xFF
    }

    fun buildSetCommand(attrId: Int, value: Int): ByteArray {
        val valueLo = (value and 0xFF).toByte()
        val valueHi = 0.toByte() // observed high byte is always 0x00, even for negative (low-byte-signed) values
        val inner = byteArrayOf(
            0x06, 0x00,
            (attrId and 0xFF).toByte(), ((attrId shr 8) and 0xFF).toByte(),
            valueLo, valueHi,
            0x00
        )
        val c = checksum(inner)
        return byteArrayOf(
            MAGIC_HI, MAGIC_LO,
            TYPE_REQUEST,
            (REQ_SEQ and 0xFF).toByte(), ((REQ_SEQ shr 8) and 0xFF).toByte(),
            (c and 0xFF).toByte(), 0x00,
            *inner
        )
    }

    sealed class Incoming {
        data class Status(val attrId: Int, val setAttrId: Int, val value: Int) : Incoming()
        data class Ack(val raw: ByteArray) : Incoming()
        data class Raw(val seq: Int, val counter: Int, val bytes: ByteArray) : Incoming()
        data class Unrecognized(val bytes: ByteArray) : Incoming()
    }

    fun parseIncoming(data: ByteArray): Incoming {
        if (data.size < 7 || data[0] != MAGIC_HI || data[1] != MAGIC_LO || data[2] != TYPE_RESPONSE) {
            return Incoming.Unrecognized(data)
        }
        val seq = (data[3].toInt() and 0xFF) or ((data[4].toInt() and 0xFF) shl 8)
        val ctr = (data[5].toInt() and 0xFF) or ((data[6].toInt() and 0xFF) shl 8)
        val rest = data.copyOfRange(7, data.size)

        // Simple numeric status echo: 06 00 <attrId u16 LE> <value u16 LE, low byte signed> 00
        if (rest.size == 7 && rest[0] == 0x06.toByte() && rest[1] == 0x00.toByte()) {
            val attrId = (rest[2].toInt() and 0xFF) or ((rest[3].toInt() and 0xFF) shl 8)
            val value = rest[4].toInt() // signed low byte
            val setAttrId = if (attrId >= STATUS_OFFSET) attrId - STATUS_OFFSET else attrId
            return Incoming.Status(attrId = attrId, setAttrId = setAttrId, value = value)
        }

        // Generic short ack seen after every write (seq=4, counter=171, payload="01 aa")
        if (rest.size <= 4) {
            return Incoming.Ack(rest)
        }

        return Incoming.Raw(seq, ctr, rest)
    }

    fun attrName(attrId: Int): String = when (attrId) {
        Attr.VOLUME -> "Volume"
        Attr.BASS -> "Bass"
        Attr.TREBLE -> "Treble"
        Attr.POWER_QUERY -> "Power"
        Attr.MUTE -> "Mute"
        Attr.SOURCE -> "Source"
        Attr.SOUND_MODE -> "Sound Mode"
        Attr.ATMOS_DTS -> "Atmos/DTS"
        Attr.GET_STATUS -> "GetStatus"
        Attr.GET_DEVICE_INFO -> "DeviceInfo"
        else -> "0x%02x".format(attrId)
    }
}
