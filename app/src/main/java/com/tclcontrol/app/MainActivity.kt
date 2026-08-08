package com.tclcontrol.app

import android.Manifest
import android.annotation.SuppressLint
import android.app.AlertDialog
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.tclcontrol.app.databinding.ActivityMainBinding

class MainActivity : AppCompatActivity(), TclSoundbarBle.Listener {

    companion object {
        private const val PREFS = "tcl_prefs"
        private const val KEY_ADDR = "last_addr"
        private const val KEY_NAME = "last_name"
        // Known TCL S45H - used as the default quick-connect target until a device has
        // actually been connected once (then the real last device wins). Must be the bonded
        // identity address, not the random address BLE scans report (that one silently fails
        // to connect via getRemoteDevice since Android assumes a public address type unless bonded).
        private const val DEFAULT_ADDR = "00:A4:1C:CD:CC:EC"
        private const val DEFAULT_NAME = "tclB14S45H0_CCEC"
    }

    private lateinit var binding: ActivityMainBinding
    private lateinit var ble: TclSoundbarBle
    private var bluetoothAdapter: BluetoothAdapter? = null
    private val logLines = ArrayDeque<String>()
    private var lastAttemptedDevice: BluetoothDevice? = null

    private var pendingAction: (() -> Unit)? = null

    private val permissionLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()
    ) { granted ->
        if (granted.values.all { it }) {
            pendingAction?.invoke()
        } else {
            appendLog("permission denied")
        }
        pendingAction = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        val btManager = getSystemService(BluetoothManager::class.java)
        bluetoothAdapter = btManager.adapter
        ble = TclSoundbarBle(this)
        ble.listener = this

        binding.btnConnect.setOnClickListener { ensurePermissionsThenPick() }
        binding.btnScan.setOnClickListener { ensurePermissionsThenScan() }
        setupReconnectButton()
        attemptAutoConnect()

        binding.btnVolUp.setOnClickListener { ble.sendCommand(TclProtocol.Attr.VOLUME, currentVolume + 1) }
        binding.btnVolDown.setOnClickListener { ble.sendCommand(TclProtocol.Attr.VOLUME, currentVolume - 1) }

        binding.switchMute.setOnCheckedChangeListener { _, checked ->
            ble.sendCommand(TclProtocol.Attr.MUTE, if (checked) 1 else 0)
        }
        binding.switchAtmos.setOnCheckedChangeListener { _, checked ->
            ble.sendCommand(TclProtocol.Attr.ATMOS_DTS, if (checked) 1 else 0)
        }

        binding.btnSource1.setOnClickListener { ble.sendCommand(TclProtocol.Attr.SOURCE, 1) }
        binding.btnSource2.setOnClickListener { ble.sendCommand(TclProtocol.Attr.SOURCE, 2) }
        binding.btnSource3.setOnClickListener { ble.sendCommand(TclProtocol.Attr.SOURCE, 3) }

        binding.btnMode1.setOnClickListener { ble.sendCommand(TclProtocol.Attr.SOUND_MODE, 1) }
        binding.btnMode2.setOnClickListener { ble.sendCommand(TclProtocol.Attr.SOUND_MODE, 2) }
        binding.btnMode3.setOnClickListener { ble.sendCommand(TclProtocol.Attr.SOUND_MODE, 3) }

        binding.seekBass.addOnChangeListener { _, value, fromUser ->
            binding.txtBassValue.text = value.toInt().toString()
            if (fromUser) ble.sendCommand(TclProtocol.Attr.BASS, value.toInt())
        }

        binding.seekTreble.addOnChangeListener { _, value, fromUser ->
            binding.txtTrebleValue.text = value.toInt().toString()
            if (fromUser) ble.sendCommand(TclProtocol.Attr.TREBLE, value.toInt())
        }

        binding.btnRefresh.setOnClickListener { ble.refreshStatus() }
    }

    private var currentVolume = 20

    private fun requiredPermissions(): Array<String> {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            arrayOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_SCAN)
        } else {
            arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
        }
    }

    private fun hasPermissions(): Boolean = requiredPermissions().all {
        ContextCompat.checkSelfPermission(this, it) == PackageManager.PERMISSION_GRANTED
    }

    private fun ensurePermissionsThenPick() {
        if (hasPermissions()) {
            showDevicePicker()
        } else {
            pendingAction = { showDevicePicker() }
            permissionLauncher.launch(requiredPermissions())
        }
    }

    private fun ensurePermissionsThenScan() {
        if (hasPermissions()) {
            startBleScan()
        } else {
            pendingAction = { startBleScan() }
            permissionLauncher.launch(requiredPermissions())
        }
    }

    private fun ensurePermissionsThen(action: () -> Unit) {
        if (hasPermissions()) {
            action()
        } else {
            pendingAction = action
            permissionLauncher.launch(requiredPermissions())
        }
    }

    private fun savedOrDefaultDevice(): Pair<String, String> {
        val prefs = getSharedPreferences(PREFS, MODE_PRIVATE)
        val addr = prefs.getString(KEY_ADDR, null) ?: DEFAULT_ADDR
        val name = prefs.getString(KEY_NAME, null) ?: DEFAULT_NAME
        return addr to name
    }

    private fun saveLastDevice(address: String, name: String) {
        getSharedPreferences(PREFS, MODE_PRIVATE).edit()
            .putString(KEY_ADDR, address)
            .putString(KEY_NAME, name)
            .apply()
    }

    @SuppressLint("MissingPermission")
    private fun connectToDevice(device: BluetoothDevice, label: String) {
        lastAttemptedDevice = device
        appendLog("connecting to $label (${device.address})")
        ble.connect(device)
    }

    private fun setupReconnectButton() {
        val (addr, name) = savedOrDefaultDevice()
        binding.btnReconnect.visibility = android.view.View.VISIBLE
        binding.btnReconnect.text = "Reconnect: $name"
        binding.btnReconnect.setOnClickListener {
            ensurePermissionsThen {
                val device = resolveDevice(addr) ?: return@ensurePermissionsThen
                connectToDevice(device, name)
            }
        }
    }

    @SuppressLint("MissingPermission")
    private fun resolveDevice(addr: String): BluetoothDevice? {
        val adapter = bluetoothAdapter ?: return null
        // Prefer the bonded object if one matches - guaranteed connectable regardless of address type.
        adapter.bondedDevices?.firstOrNull { it.address.equals(addr, ignoreCase = true) }?.let { return it }
        return adapter.getRemoteDevice(addr)
    }

    private fun attemptAutoConnect() {
        if (!hasPermissions()) return
        val (addr, name) = savedOrDefaultDevice()
        val device = resolveDevice(addr) ?: return
        connectToDevice(device, name)
    }

    private val scanResults = LinkedHashMap<String, BluetoothDevice>()
    private var scanning = false
    private val scanHandler = Handler(Looper.getMainLooper())

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            val device = result.device
            val label = device.name ?: return
            scanResults[device.address] = device
            appendLog("found: $label (${device.address}) rssi=${result.rssi}")
        }

        override fun onScanFailed(errorCode: Int) {
            scanning = false
            appendLog("scan failed, error=$errorCode")
        }
    }

    @SuppressLint("MissingPermission")
    private fun startBleScan() {
        val scanner = bluetoothAdapter?.bluetoothLeScanner ?: run {
            appendLog("bluetooth not available/enabled")
            return
        }
        if (scanning) return
        scanResults.clear()
        scanning = true
        appendLog("scanning for 8s...")
        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .build()
        scanner.startScan(null, settings, scanCallback)
        scanHandler.postDelayed({
            scanner.stopScan(scanCallback)
            scanning = false
            showScanResultsDialog()
        }, 8000)
    }

    @SuppressLint("MissingPermission")
    private fun showScanResultsDialog() {
        if (scanResults.isEmpty()) {
            appendLog("no BLE devices found - make sure the soundbar is powered on and TCL Home is closed")
            return
        }
        val devices = scanResults.values.toList()
        val names = devices.map { "${it.name ?: "?"} (${it.address})" }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle("Select soundbar")
            .setItems(names) { _, which ->
                connectToDevice(devices[which], devices[which].name ?: devices[which].address)
            }
            .show()
    }

    private fun showDevicePicker() {
        val adapter = bluetoothAdapter ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ActivityCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            appendLog("missing BLUETOOTH_CONNECT")
            return
        }
        val bonded: Set<BluetoothDevice> = adapter.bondedDevices ?: emptySet()
        val names = bonded.map { "${it.name ?: "?"} (${it.address})" }.toTypedArray()
        val devices = bonded.toList()
        if (devices.isEmpty()) {
            appendLog("no paired devices - pair with the soundbar in Android Bluetooth settings first")
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Select soundbar")
            .setItems(names) { _, which ->
                connectToDevice(devices[which], devices[which].name ?: devices[which].address)
            }
            .show()
    }

    @SuppressLint("MissingPermission")
    override fun onConnectionState(connected: Boolean) {
        binding.connectionStatus.text = if (connected) "Connected" else "Not connected"
        binding.statusDot.setBackgroundResource(if (connected) R.drawable.dot_success else R.drawable.dot_danger)
        if (connected) {
            val device = lastAttemptedDevice
            if (device != null) {
                val name = device.name ?: DEFAULT_NAME
                saveLastDevice(device.address, name)
                binding.btnReconnect.text = "Reconnect: $name"
            }
        }
    }

    override fun onStatus(status: TclProtocol.Incoming.Status) {
        appendLog("STATUS ${TclProtocol.attrName(status.setAttrId)} = ${status.value}")
        when (status.setAttrId) {
            TclProtocol.Attr.VOLUME -> {
                currentVolume = status.value
                binding.txtVolume.text = status.value.toString()
            }
            TclProtocol.Attr.MUTE -> binding.switchMute.isChecked = status.value == 1
            TclProtocol.Attr.ATMOS_DTS -> binding.switchAtmos.isChecked = status.value == 1
            TclProtocol.Attr.BASS -> {
                binding.seekBass.value = status.value.toFloat().coerceIn(-6f, 6f)
                binding.txtBassValue.text = status.value.toString()
            }
            TclProtocol.Attr.TREBLE -> {
                binding.seekTreble.value = status.value.toFloat().coerceIn(-6f, 6f)
                binding.txtTrebleValue.text = status.value.toString()
            }
            TclProtocol.Attr.SOURCE -> checkToggle(binding.toggleSource, status.value, binding.btnSource1.id, binding.btnSource2.id, binding.btnSource3.id)
            TclProtocol.Attr.SOUND_MODE -> checkToggle(binding.toggleMode, status.value, binding.btnMode1.id, binding.btnMode2.id, binding.btnMode3.id)
        }
    }

    private fun checkToggle(group: com.google.android.material.button.MaterialButtonToggleGroup, value: Int, id1: Int, id2: Int, id3: Int) {
        val id = when (value) { 1 -> id1; 2 -> id2; 3 -> id3; else -> null }
        if (id != null) group.check(id) else group.clearChecked()
    }

    override fun onLog(line: String) = appendLog(line)

    private fun appendLog(line: String) {
        logLines.addFirst(line)
        while (logLines.size > 30) logLines.removeLast()
        binding.logView.text = logLines.joinToString("\n")
    }

    override fun onDestroy() {
        super.onDestroy()
        ble.disconnect()
    }
}
