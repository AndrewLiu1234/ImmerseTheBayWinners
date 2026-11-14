# XREAL Air-family IMU over USB HID (for `tools/imu-bridge`)

*Person 1 · Sat 2026-11-14 ~01:40 PT · spike M-S2 / slice SL8. Every byte the bridge sends or parses is
quoted below from a source, with the revision read. Nobody has run this against our Air 2 Ultra yet:
§1 says which facts the sources state for the Ultra and which come only from code shared with older Airs.*

Code: `tools/imu-bridge/xreal_imu.py` (pure: parsing, commands, fusion, wire format) and
`tools/imu-bridge/bridge.py` (hidapi + UDP). hidapi calls: `docs/apis/python/hidapi.md`.

## 0. Sources read

| Key | What | Revision read | URL |
|---|---|---|---|
| M | Monado, `src/xrt/drivers/xreal_air/` + its prober (`xreal_air_interface.h`, `xreal_air_hmd.h`, `xreal_air_hmd.c`, `xreal_air_packet.c`; there is no `xreal_air_packet.h`, the packet declarations are in `xreal_air_hmd.h`) | `9dc151cd2efae21d1308fcc4e974b6c9a97a7513` (main, 2026-11-14) | https://gitlab.freedesktop.org/monado/monado/-/tree/9dc151cd2efae21d1308fcc4e974b6c9a97a7513/src/xrt/drivers/xreal_air |
| M-2436 | Monado MR !2436 "Implement changes to support Xreal Air 2 Ultra", commit `80040ff32336d1cbf15b0dc78fe8a5a18b86040c`, merged 2025-07-04 | as merged | https://gitlab.freedesktop.org/monado/monado/-/merge_requests/2436 |
| M-3090 | Monado MR !3090 "a/os, st/prober: Add a hidapi backend" (first HID path for macOS), merged 2026-11-13 | as merged | https://gitlab.freedesktop.org/monado/monado/-/merge_requests/3090 |
| X | xrealAirLinuxDriver by TheJackiMonster (the author of M's driver) | `9a1f55c9838cf92627cde62f9bd69269d213d134` (2025-08-15) | https://github.com/TheJackiMonster/xrealAirLinuxDriver |
| R | ar-drivers-rs 0.4.3 (badicsalex) | `56bfa489ab697706ea95b36c2e43c5ca15d6a263` (2026-05-14) | https://github.com/badicsalex/ar-drivers-rs |
| R-23 | ar-drivers-rs PR #23 "Add support for XREAL Air 2 Ultra", merged 2026-01-19 as `d3dcfe8b267e69e43aba96bd36044c14b09a90d9` | as merged | https://github.com/badicsalex/ar-drivers-rs/pull/23 |
| F | Fusion (xioTechnologies), the commit X pins as a submodule | `b607fc32d2d8dd1ac5fb9f81ca8244dda6e58ddf` | https://github.com/xioTechnologies/Fusion |
| V | "Worse, better, prettier", the protocol write-up by R's author (original Air) | fetched 2026-11-14 | https://voidcomputing.hu/blog/worse-better-prettier/#the-prettier-xreal-air |
| B | bevy-xreal-ar-demo README (IMU on macOS through R) | `faa0b6d0f1f5080aa4089c04457a6e6e8c2c8b5d` | https://github.com/SimDoes/bevy-xreal-ar-demo |
| U | UnityCsReference `Runtime/Export/Math/Quaternion.cs` (branch 2021.3) and the Unity 2021.3 Scripting API | `b052b1fe38b45d5aefa609a115c626ed2e74f3a6` | https://github.com/Unity-Technologies/UnityCsReference/blob/2021.3/Runtime/Export/Math/Quaternion.cs · https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Quaternion.Euler.html |

Code excerpts are byte-for-byte copies (tabs kept) of the stated lines at the stated revision; prose
quotes are verbatim. Each carries a `<!-- verbatim … -->` or `<!-- quote … -->` marker so a script can
re-check it against the sources.

## 1. What the sources confirm for the Air 2 Ultra

"Shared code path" below means: the source supports the Ultra with the same function it uses for the
older Airs, and its Ultra-specific changes are only the product id, the interface numbers and the buffer
size. That is good evidence, but no source shows a captured Ultra packet, so those rows stay
**UNVERIFIED FOR ULTRA** until spike M-S2 (§11).

| Fact | Value the bridge uses | Sources | Ultra status |
|---|---|---|---|
| USB vendor id | `0x3318` | M, X, R (§2) | Confirmed: the Ultra entry uses it in all three |
| Product id | Ultra `0x0426` (Air `0x0424`, Air 2 `0x0428`, Air 2 Pro `0x0432`) | M, X, R (§2) | **Confirmed for the Ultra** (Ultra-specific entries) |
| IMU HID interface | Ultra **2** (older: 3) | M, X, R (§2) | **Confirmed for the Ultra** |
| MCU/control interface | Ultra **0** (older: 4). The bridge does not open it | M, X, R (§2) | **Confirmed for the Ultra** |
| IMU-interface report size | Ultra **512** bytes (older: 64) | M, X, R (§2) | **Confirmed for the Ultra** |
| Command frame + CRC-32 | §3 | M, X, R, V | UNVERIFIED FOR ULTRA (shared code path; R-23's author says the code was tested on an Ultra) |
| Stream on/off: msg `0x19` with `0x01`/`0x00` | §3 | M, X, R, V | UNVERIFIED FOR ULTRA (shared code path, same) |
| IMU packet layout, header `01 02` | §5 | M, X, R, V | UNVERIFIED FOR ULTRA (shared code path, same) |
| Units: deg/s, g, ns; value = raw × multiplier ÷ divisor | §5 | M, X, R, V | UNVERIFIED FOR ULTRA (shared code path) |
| Axis orientation (device → right/up/back) | §7 | M, X, R | UNVERIFIED FOR ULTRA (shared code path). The human test in §8 checks it |
| Sample rate | ~1000 Hz | X (a constant, §6) | UNVERIFIED FOR ULTRA, and measured by no source. The bridge prints the measured rate |
| Temperature formula | raw ÷ 132.48 + 25 °C | M, X (§5) | UNVERIFIED FOR ULTRA (V names a different IMU chip for the original Air). Display only |
| Magnetometer | parsed, not used | M, X (§5) | UNVERIFIED FOR ULTRA; unit undocumented |
| macOS | hidapi, as in R | B, M-3090 (§10) | UNVERIFIED: B reports the Air/Air 2/Air 2 Pro IMU on macOS; nobody reports the Ultra on macOS |

## 2. USB ids and HID interfaces

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_interface.h L23-L56 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_interface.h`, lines 23-56:
```c
/*!
 * Vendor id for Xreal Air.
 *
 * @ingroup drv_xreal_air
 */
#define XREAL_AIR_VID 0x3318

/*!
 * Product id for Xreal Air.
 *
 * @ingroup drv_xreal_air
 */
#define XREAL_AIR_PID 0x0424

/*!
 * Product id for Xreal Air 2.
 *
 * @ingroup drv_xreal_air
 */
#define XREAL_AIR_2_PID 0x0428

/*!
 * Product id for Xreal Air 2 Pro.
 *
 * @ingroup drv_xreal_air
 */
#define XREAL_AIR_2_PRO_PID 0x0432

/*!
 * Product id for Xreal Air 2 Ultra.
 *
 * @ingroup drv_xreal_air
 */
#define XREAL_AIR_2_ULTRA_PID 0x0426
```

Interfaces and buffer sizes per model, and how they are opened:

<!-- verbatim: M src/xrt/targets/common/target_builder_xreal_air.c L52-L80 -->
Monado `src/xrt/targets/common/target_builder_xreal_air.c`, lines 52-80:
```c
#define XREAL_AIR_DRIVER_PRODUCT_IDS 4

static const uint16_t driver_product_ids[XREAL_AIR_DRIVER_PRODUCT_IDS] = {
    XREAL_AIR_PID,
    XREAL_AIR_2_PID,
    XREAL_AIR_2_PRO_PID,
    XREAL_AIR_2_ULTRA_PID,
};

static const uint16_t driver_handle_ifaces[XREAL_AIR_DRIVER_PRODUCT_IDS] = {
    3,
    3,
    3,
    2,
};

static const uint16_t driver_control_ifaces[XREAL_AIR_DRIVER_PRODUCT_IDS] = {
    4,
    4,
    4,
    0,
};

static const uint16_t driver_max_sensor_buffer_sizes[XREAL_AIR_DRIVER_PRODUCT_IDS] = {
    64,
    64,
    64,
    512,
};
```

<!-- verbatim: M src/xrt/targets/common/target_builder_xreal_air.c L164-L173 -->
Monado `src/xrt/targets/common/target_builder_xreal_air.c`, lines 164-173:
```c
	struct h_hid_device *hid_handle = NULL;
	int result = xrt_prober_open_hid_interface(xp, dev_hmd, driver_handle_ifaces[product_index], &hid_handle);

	if (result != 0) {
		XREAL_AIR_ERROR("Failed to open Xreal Air handle interface");
		goto unlock_and_fail;
	}

	struct h_hid_device *hid_control = NULL;
	result = xrt_prober_open_hid_interface(xp, dev_hmd, driver_control_ifaces[product_index], &hid_control);
```

`driver_handle_ifaces` is the IMU ("sensor") interface: it becomes `hmd->hid_sensor`, the device all IMU
commands and packets in §3–§5 use. The Ultra-support MR says why the Ultra differs:

<!-- quote: gitlab-api/mr2436_description.txt -->
> These changes should add support for the Xreal Air 2 Ultra. It requires to use a different buffer size to read the calibration data from the IMU sensor interface and it uses interfaces in a different order. So most of the driver for the Xreal Air and following devices can still be reused.

The same numbers in X (the Ultra entries came in X commits `28eb02d` and `fc9eb2d`, 2024-07, and the
512-byte size in `c5e051d` "Fix loading calibration data from Xreal Air 2 Ultra", 2025-03) and in R:

<!-- verbatim: X interface_lib/src/hid_ids.c L37-L64 -->
xrealAirLinuxDriver `interface_lib/src/hid_ids.c`, lines 37-64:
```c
const uint16_t xreal_vendor_id = 0x3318;
const uint16_t xreal_product_ids[NUM_SUPPORTED_PRODUCTS] = {
    0x0424, // XREAL Air
    0x0428, // XREAL Air 2
    0x0432, // XREAL Air 2 Pro
    0x0426  // XREAL Air 2 Ultra
};

const int xreal_imu_interface_ids[NUM_SUPPORTED_PRODUCTS] = {
    3, // XREAL Air
    3, // XREAL Air 2
    3, // XREAL Air 2 Pro
    2  // XREAL Air 2 Ultra
};

const int xreal_mcu_interface_ids[NUM_SUPPORTED_PRODUCTS] = {
    4,  // XREAL Air
    4,  // XREAL Air 2
    4,  // XREAL Air 2 Pro
    0   // XREAL Air 2 Ultra MCU
};

const uint16_t xreal_imu_max_payload_sizes[NUM_SUPPORTED_PRODUCTS] = {
    64, // XREAL Air
    64, // XREAL Air 2
    64, // XREAL Air 2 Pro
    512 // XREAL Air 2 Ultra
};
```

<!-- verbatim: R src/nreal_air.rs L66-L90 -->
ar-drivers-rs `src/nreal_air.rs`, lines 66-90:
```rust
impl AirModel {
    /// Returns the MCU/command interface number for this model
    fn mcu_interface(&self) -> i32 {
        match self {
            AirModel::Air2Ultra => 0,
            _ => 4,
        }
    }

    /// Returns the IMU interface number for this model
    fn imu_interface(&self) -> i32 {
        match self {
            AirModel::Air2Ultra => 2,
            _ => 3,
        }
    }

    /// Returns the maximum IMU packet size for this model
    fn imu_packet_size(&self) -> usize {
        match self {
            AirModel::Air2Ultra => 0x200,
            _ => 0x40,
        }
    }
}
```

<!-- quote: github-api/pr23_body.txt -->
> Backported values from [xrealAirLinuxDriver](https://github.com/TheJackiMonster/xrealAirLinuxDriver) to support XREAL Air 2 Ultra devices. The code has been tested only for Ultra device (the only one I have right now).

X finds the IMU interface by `interface_number`, which is also how the bridge does it (hidapi fills
`interface_number` on macOS too: `docs/apis/python/hidapi.md`):

<!-- verbatim: X interface_lib/src/device_imu.c L371-L390 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 371-390:
```c
	struct hid_device_info* info = hid_enumerate(
		device->vendor_id, 
		device->product_id
	);

	struct hid_device_info* it = info;
	while (it) {
		int interface_id = xreal_imu_interface_id(it->product_id);
		if (interface_id != -1 && it->interface_number == interface_id) {
#ifndef NDEBUG
            printf("Found IMU device with product_id 0x%x on interface %d\n", it->product_id, interface_id);
#endif
			device->product_id = it->product_id;
			device->handle = hid_open_path(it->path);
			device->max_payload_size = xreal_imu_max_payload_size(device->product_id);
			break;
		}

		it = it->next;
	}
```

For the original Air, V adds the endpoints (not stated for the Ultra by any source):

<!-- quote: voidcomputing-dl/wbp.txt -->
> IMU packets are also sent/received with regular HID read() and write(), over interface 3 (endpoints 0x84 and 0x05), with 0x40-sized packets.

The bridge opens only the IMU interface. X's IMU code (`device_imu_open`, used alone by its
`examples/debug_imu`) never touches the MCU interface; M and R also open the MCU interface, but for
brightness, display mode, buttons and the serial number, not for IMU data.

## 3. Commands to the IMU interface

Frame layout, as M builds it:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L193-L216 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 193-216:
```c
static bool
send_payload_to_sensor(struct xreal_air_hmd *hmd, uint8_t msgid, const uint8_t *data, uint8_t len)
{
	uint8_t payload[SENSOR_BUFFER_SIZE];

	const uint16_t packet_len = 3 + len;
	const uint16_t payload_len = 5 + packet_len;

	payload[0] = 0xAA;
	payload[5] = (packet_len & 0xFFu);
	payload[6] = ((packet_len >> 8u) & 0xFFu);
	payload[7] = msgid;

	memcpy(payload + 8, data, len);

	const uint32_t checksum = crc32_checksum(payload + 5, packet_len);

	payload[1] = (checksum & 0xFFu);
	payload[2] = ((checksum >> 8u) & 0xFFu);
	payload[3] = ((checksum >> 16u) & 0xFFu);
	payload[4] = ((checksum >> 24u) & 0xFFu);

	return h_hid_write(hmd->hid_sensor, payload, payload_len);
}
```

So a command is `[0] 0xAA · [1..4] CRC-32, little endian · [5..6] length = 3 + n, little endian ·
[7] msgid · [8..] n data bytes`, and the CRC covers bytes `[5 .. 5+length)`, which is the length field,
the msgid and the data. X builds the same frame (`packet.length` is at offset 5 of a packed struct):

<!-- verbatim: X interface_lib/src/device_imu.c L139-L168 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 139-168:
```c
struct __attribute__((__packed__)) device_imu_payload_packet_t {
	uint8_t head;
	uint32_t checksum;
	uint16_t length;
	uint8_t msgid;
	uint8_t data [512 - 8];
};

typedef struct device_imu_payload_packet_t device_imu_payload_packet_type;

static bool send_payload_msg(device_imu_type* device, uint8_t msgid, uint16_t len, const uint8_t* data) {
	static device_imu_payload_packet_type packet;
	
	const uint16_t packet_len = 3 + len;
	const uint16_t payload_len = 5 + packet_len;
	
	packet.head = 0xAA;
	packet.length = htole16(packet_len);
	packet.msgid = msgid;
	
	memcpy(packet.data, data, len);
	packet.checksum = htole32(
		crc32_checksum(
			(const uint8_t*) (&packet.length),
			packet.length
		)
	);
	
	return send_payload(device, payload_len, (uint8_t*) (&packet));
}
```

### CRC

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L158-L168 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 158-168:
```c
static uint32_t
crc32_checksum(const uint8_t *buf, uint32_t len)
{
	uint32_t CRC32_data = 0xFFFFFFFF;
	for (uint32_t i = 0; i < len; i++) {
		const uint8_t t = (CRC32_data ^ buf[i]) & 0xFFu;
		CRC32_data = ((CRC32_data >> 8u) & 0xFFFFFFu) ^ crc32_table[t];
	}

	return ~CRC32_data;
}
```

`crc32_table` (M `xreal_air_hmd.c` lines 127–156) starts and ends like this:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L127-L128 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 127-128:
```c
const uint32_t crc32_table[256] = {
    0x00000000, 0x77073096, 0xEE0E612C, 0x990951BA, 0x076DC419, 0x706AF48F, 0xE963A535, 0x9E6495A3, 0x0EDB8832,
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L155-L156 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 155-156:
```c
    0x24B4A3A6, 0xBAD03605, 0xCDD70693, 0x54DE5729, 0x23D967BF, 0xB3667A2E, 0xC4614AB8, 0x5D681B02, 0x2A6F2B94,
    0xB40BBE37, 0xC30C8EA1, 0x5A05DF1B, 0x2D02EF8D};
```

R uses the same table under the name `crc32_adler` (V also says "CRC32(Adler)"); the loop:

<!-- verbatim: R src/util.rs L87-L93 -->
ar-drivers-rs `src/util.rs`, lines 87-93:
```rust
    let mut r = 0xffffffffu32;
    for &byte in buf.iter() {
        let idx = byte ^ ((r & 0xff) as u8);
        r = (r >> 8) ^ CRCTABLE[idx as usize];
    }

    r ^ 0xffffffffu32
```

Checked here (script, 2026-11-14): all 256 entries of M's table equal the standard reflected CRC-32 table
for polynomial `0xEDB88320`; M's loop equals Python's `zlib.crc32` on 2000 random buffers; the check
value `crc32(b"123456789")` is `0xCBF43926`. So the algorithm is plain CRC-32 (IEEE), not Adler-32.

### Message ids

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.h L66-L73 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.h`, lines 66-73:
```c
#define XREAL_AIR_MSG_GET_CAL_DATA_LENGTH 0x14
#define XREAL_AIR_MSG_CAL_DATA_GET_NEXT_SEGMENT 0x15
#define XREAL_AIR_MSG_ALLOCATE_CAL_DATA_BUFFER 0x16
#define XREAL_AIR_MSG_WRITE_CAL_DATA_SEGMENT 0x17
#define XREAL_AIR_MSG_FREE_CAL_BUFFER 0x18
#define XREAL_AIR_MSG_START_IMU_DATA 0x19
#define XREAL_AIR_MSG_GET_STATIC_ID 0x1A
#define XREAL_AIR_MSG_UNKNOWN 0x1D
```

<!-- quote: voidcomputing-dl/wbp.txt -->
> Enable IMU stream 0x19 0: disable, 1: Enable

How each driver uses `0x19`. M switches the stream on with `0x01` once it has read the calibration:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L393-L401 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 393-401:
```c
static void
request_sensor_control_start_imu_data(struct xreal_air_hmd *hmd, uint8_t imu_stream_state)
{
	// Request to change the imu stream state.

	if (!send_payload_to_sensor(hmd, XREAL_AIR_MSG_START_IMU_DATA, &imu_stream_state, 1)) {
		XREAL_AIR_ERROR(hmd, "Failed to send payload for changing the imu stream state! %d", imu_stream_state);
	}
}
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L471-L483 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 471-483:
```c
	if (hmd->calibration_buffer_pos == hmd->calibration_buffer_len) {
		// Parse calibration data from raw json.
		if (!xreal_air_parse_calibration_buffer(&hmd->calibration, hmd->calibration_buffer,
		                                        hmd->calibration_buffer_len)) {
			hmd->calibration_valid = false;

			XREAL_AIR_ERROR(hmd, "Failed parse calibration data!");
		} else {
			hmd->calibration_valid = true;

			// Switch to imu sensor data stream
			request_sensor_control_start_imu_data(hmd, 0x01);
		}
```

M's read thread first sends `0x19` with the value `0xAA`, and sends it again whenever a sensor packet
arrives before the calibration is valid (`handle_sensor_msg`, §5). No source explains `0xAA` as a
stream state, so the bridge never sends it:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L641-L643 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 641-643:
```c
	os_thread_helper_lock(&hmd->oth);

	request_sensor_control_start_imu_data(hmd, 0xAA);
```

X: off (`0x0`) before reading the calibration, on (`0x1`) after it, and off again on close:

<!-- verbatim: X interface_lib/src/device_imu.c L399-L403 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 399-403:
```c
	if ((!send_payload_msg_signal(device, DEVICE_IMU_MSG_START_IMU_DATA, 0x0)) ||
        (!recv_payload_msg(device, DEVICE_IMU_MSG_START_IMU_DATA, 0, NULL))) {
		device_imu_error("Failed sending payload to stop imu data stream");
		return DEVICE_IMU_ERROR_PAYLOAD_FAILED;
	}
```

<!-- verbatim: X interface_lib/src/device_imu.c L489-L493 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 489-493:
```c
	if ((!send_payload_msg_signal(device, DEVICE_IMU_MSG_START_IMU_DATA, 0x1)) ||
		(!recv_payload_msg(device, DEVICE_IMU_MSG_START_IMU_DATA, 0, NULL))) {
		device_imu_error("Failed sending payload to start imu data stream");
		return DEVICE_IMU_ERROR_PAYLOAD_FAILED;
	}
```

<!-- verbatim: X interface_lib/src/device_imu.c L1342-L1349 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 1342-1349:
```c
	if (device->handle) {
		if ((!send_payload_msg_signal(device, DEVICE_IMU_MSG_START_IMU_DATA, 0x0)) ||
			(!recv_payload_msg(device, DEVICE_IMU_MSG_START_IMU_DATA, 0, NULL))) {
			device_imu_error("Failed sending payload to stop imu data stream");
		}

		hid_close(device->handle);
	}
```

R (tested on an Ultra, R-23):

<!-- verbatim: R src/nreal_air.rs L325-L342 -->
ar-drivers-rs `src/nreal_air.rs`, lines 325-342:
```rust
    fn new_device(device: HidDevice, model: AirModel) -> Result<Self> {
        let mut result = Self {
            device,
            model,
            config_json: JsonValue::Null,
            displays: None,
            gyro_bias: Default::default(),
            accelerometer_bias: Default::default(),
        };
        // Turn off IMU stream while reading config
        result.command(0x19, &[0x0])?;
        result.read_config()?;
        result.parse_config()?;
        // Turn IMU stream back on
        result.command(0x19, &[0x1])?;

        Ok(result)
    }
```

### The bytes the bridge sends

Computed with the frame and CRC above (`xreal_imu.build_command`, re-derived by an independent scratch
script; `test_xreal_imu.py` checks the code against these lines):

<!-- test-bytes: start_imu_exact -->
```
AA C5 D1 21 42 04 00 19 01
```

<!-- test-bytes: stop_imu_exact -->
```
AA 53 E1 26 35 04 00 19 00
```

Corroboration (inference): X treats a report that starts `0xaa 0x53` as "initialized" (§5, line 1104);
`AA 53` is exactly how a `0x19`/`0x00` frame starts, i.e. the reply to X's own "stream off" command if
the glasses answer with the same frame. That matches our CRC.

**Write length.** M (`payload_len = 5 + packet_len`) and X (`send_payload(device, payload_len, …)`)
write exactly 8 + n bytes (9 for `0x19`). R writes a zero-padded 64-byte buffer (`serialize()` returns
`[u8; 0x40]`), with a leading `0x00` only on Windows:

<!-- verbatim: R src/nreal_air.rs L676-L690 -->
ar-drivers-rs `src/nreal_air.rs`, lines 676-690:
```rust
#[cfg(target_os = "windows")]
fn write_hid_packet(device: &HidDevice, payload: &[u8; 0x40]) -> Result<()> {
    // Windows HID writes need the leading report ID byte even for unnumbered reports.
    // The Air replies on interfaces 3/4 once the payload is sent as [0x00 | 64-byte packet].
    let mut report = [0u8; 0x41];
    report[1..].copy_from_slice(payload);
    device.write(&report)?;
    Ok(())
}

#[cfg(not(target_os = "windows"))]
fn write_hid_packet(device: &HidDevice, payload: &[u8; 0x40]) -> Result<()> {
    device.write(payload)?;
    Ok(())
}
```

<!-- verbatim: X interface_lib/src/device_imu.c L99-L113 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 99-113:
```c
static bool send_payload(device_imu_type* device, uint16_t size, const uint8_t* payload) {
	int payload_size = size;
	if (payload_size > device->max_payload_size) {
		payload_size = device->max_payload_size;
	}
	
	int transferred = hid_write(device->handle, payload, payload_size);
	
	if (transferred != payload_size) {
		device_imu_error("Sending payload failed");
		return false;
	}
	
	return (transferred == size);
}
```

The bridge writes R's 64-byte form first (R is the only source with an Ultra test, and B, a macOS demo,
uses R). If no IMU packet arrives within 2 s it retries the exact 9-byte form, then both forms with a
leading `0x00` (R's Windows form; on macOS hidapi strips a leading `0x00` before sending, see
`docs/apis/python/hidapi.md`). Padding is never part of the CRC.

## 4. Replies on the IMU interface

Reports that start `0xAA` are replies to commands; anything else is treated as a sensor packet:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L525-L547 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 525-547:
```c
handle_sensor_control_data_msg(struct xreal_air_hmd *hmd, unsigned char *buffer, size_t size)
{
	struct xreal_air_parsed_sensor_control_data data;

	if (!xreal_air_parse_sensor_control_data_packet(&data, buffer, size, hmd->max_sensor_buffer_size)) {
		XREAL_AIR_ERROR(hmd, "Could not decode sensor control data packet");
	}

	hmd->imu_stream_state = 0xAA;

	switch (data.msgid) {
	case XREAL_AIR_MSG_GET_CAL_DATA_LENGTH: handle_sensor_control_get_cal_data_length(hmd, &data); break;
	case XREAL_AIR_MSG_CAL_DATA_GET_NEXT_SEGMENT:
		handle_sensor_control_cal_data_get_next_segment(hmd, &data);
		break;
	case XREAL_AIR_MSG_ALLOCATE_CAL_DATA_BUFFER: break;
	case XREAL_AIR_MSG_WRITE_CAL_DATA_SEGMENT: break;
	case XREAL_AIR_MSG_FREE_CAL_BUFFER: break;
	case XREAL_AIR_MSG_START_IMU_DATA: handle_sensor_control_start_imu_data(hmd, &data); break;
	case XREAL_AIR_MSG_GET_STATIC_ID: handle_sensor_control_get_static_id(hmd, &data); break;
	default: XREAL_AIR_ERROR(hmd, "Got unknown sensor control data msgid, 0x%02x", data.msgid); break;
	}
}
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_packet.c L314-L342 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_packet.c`, lines 314-342:
```c
bool
xreal_air_parse_sensor_control_data_packet(struct xreal_air_parsed_sensor_control_data *data,
                                           const uint8_t *buffer,
                                           size_t size,
                                           size_t max_size)
{
	const uint8_t *start = buffer;

	if ((size != max_size) || (size < 8)) {
		return false;
	}

	// Header
	skip(&buffer, 1);

	// Checksum
	skip(&buffer, 4);

	// Length
	read_u16(&buffer, &data->length);

	// MSGID
	read_u8(&buffer, &data->msgid);

	// Sensor control data depending on action
	read_u8_array(&buffer, data->data, size - 8);

	return (size_t)buffer - (size_t)start == max_size;
}
```

The reply to `0x19` carries the stream state in its first data byte:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L495-L508 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 495-508:
```c
static void
handle_sensor_control_start_imu_data(struct xreal_air_hmd *hmd, const struct xreal_air_parsed_sensor_control_data *data)
{
	// Read the imu stream state.
	const uint8_t imu_stream_state = data->data[0];

	hmd->imu_stream_state = imu_stream_state;

	if (hmd->static_id == 0) {
		request_sensor_control_get_static_id(hmd);
	} else if ((!hmd->calibration_valid) && (!hmd->calibration_buffer_len)) {
		request_sensor_control_get_cal_data_length(hmd);
	}
}
```

No source checks a reply's CRC (R: `// TODO: maybe check CRC?`). The bridge only logs replies.

## 5. IMU data packet

M reads up to `max_sensor_buffer_size` bytes (512 on the Ultra, §2), dispatches on the first byte, and
parses the first 64:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L611-L628 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 611-628:
```c
static int
sensor_read_one_packet(struct xreal_air_hmd *hmd)
{
	uint8_t buffer[SENSOR_BUFFER_SIZE];
	size_t buffer_size = SENSOR_BUFFER_SIZE;

	if (buffer_size > hmd->max_sensor_buffer_size) {
		buffer_size = hmd->max_sensor_buffer_size;
	}

	int size = h_hid_read(hmd->hid_sensor, buffer, buffer_size, 0);
	if (size <= 0) {
		return size;
	}

	handle_sensor_msg(hmd, buffer, (size_t)size);
	return 0;
}
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L549-L594 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 549-594:
```c
static void
handle_sensor_msg(struct xreal_air_hmd *hmd, unsigned char *buffer, size_t size)
{
	if (buffer[0] == 0xAA) {
		handle_sensor_control_data_msg(hmd, buffer, size);
		return;
	}

	timepoint_ns now_ns = (timepoint_ns)os_monotonic_get_ns();
	uint64_t last_timestamp = hmd->last.timestamp;

	if (!xreal_air_parse_sensor_packet(&hmd->last, buffer, size, hmd->max_sensor_buffer_size)) {
		XREAL_AIR_ERROR(hmd, "Could not decode sensor packet");
	} else {
		hmd->imu_stream_state = 0x1;
	}

	if (!hmd->calibration_valid) {
		request_sensor_control_start_imu_data(hmd, 0xAA);
	}

	struct xreal_air_parsed_sensor *s = &hmd->last;

	// According to the ICM-42688-P datasheet: (offset: 25 °C, sensitivity: 132.48 LSB/°C)
	hmd->read.temperature = ((float)s->temperature) / 132.48f + 25.0f;

	uint32_t delta = calc_delta_and_handle_rollover(s->timestamp, last_timestamp);

	time_duration_ns inter_sample_duration_ns = delta;

	// If this is larger then one second something bad is going on.
	if (hmd->fusion.state != M_IMU_3DOF_STATE_START &&
	    inter_sample_duration_ns >= (time_duration_ns)U_TIME_1S_IN_NS) {
		XREAL_AIR_ERROR(hmd, "Drop packet (sensor too slow): %" PRId64, inter_sample_duration_ns);
		return;
	}

	// Move it back in time.
	timepoint_ns timestamp_ns = now_ns - inter_sample_duration_ns;

	// Make sure timestamps are always after a previous timestamp.
	timestamp_ns = ensure_forward_progress_timestamps(hmd, timestamp_ns);

	// Update the fusion with the sample.
	update_fusion(hmd, &s->sample, timestamp_ns);
}
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_packet.c L277-L312 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_packet.c`, lines 277-312:
```c
bool
xreal_air_parse_sensor_packet(struct xreal_air_parsed_sensor *sensor,
                              const uint8_t *buffer,
                              size_t size,
                              size_t max_size)
{
	const uint8_t *start = buffer;

	if ((size != max_size) || (size < 64)) {
		return false;
	}

	if (buffer[0] != 1) {
		return false;
	}

	// Header
	skip(&buffer, 2);

	// Temperature
	read_i16(&buffer, &sensor->temperature);

	// Timestamp
	read_u64(&buffer, &sensor->timestamp);

	// Sample
	read_sample(&buffer, &sensor->sample);

	// Checksum
	skip(&buffer, 4);

	// Unknown, skip 6 bytes.
	skip(&buffer, 6);

	return (size_t)buffer - (size_t)start == 64;
}
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_packet.c L205-L228 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_packet.c`, lines 205-228:
```c
static void
read_sample(const uint8_t **buffer, struct xreal_air_parsed_sample *sample)
{
	read_i16(buffer, &sample->gyro_multiplier);
	read_i32(buffer, &sample->gyro_divisor);

	read_i24_to_i32(buffer, &sample->gyro.x);
	read_i24_to_i32(buffer, &sample->gyro.y);
	read_i24_to_i32(buffer, &sample->gyro.z);

	read_i16(buffer, &sample->accel_multiplier);
	read_i32(buffer, &sample->accel_divisor);

	read_i24_to_i32(buffer, &sample->accel.x);
	read_i24_to_i32(buffer, &sample->accel.y);
	read_i24_to_i32(buffer, &sample->accel.z);

	read_i16_rev(buffer, &sample->mag_multiplier);
	read_i32_rev(buffer, &sample->mag_divisor);

	read_i15_to_i32(buffer, &sample->mag.x);
	read_i15_to_i32(buffer, &sample->mag.y);
	read_i15_to_i32(buffer, &sample->mag.z);
}
```

The byte readers it uses:

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_packet.c L30-L85 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_packet.c`, lines 30-85:
```c
static inline void
read_i16(const uint8_t **buffer, int16_t *out_value)
{
	*out_value = (*(*buffer + 0) << 0u) | // Byte 0
	             (*(*buffer + 1) << 8u);  // Byte 1
	*buffer += 2;
}

static inline void
read_i24_to_i32(const uint8_t **buffer, int32_t *out_value)
{
	*out_value = (*(*buffer + 0) << 0u) | // Byte 0
	             (*(*buffer + 1) << 8u) | // Byte 1
	             (*(*buffer + 2) << 16u); // Byte 2
	if (*(*buffer + 2) & 0x80u)
		*out_value |= (0xFFu << 24u); // Properly sign extend.
	*buffer += 3;
}

static inline void
read_i32(const uint8_t **buffer, int32_t *out_value)
{
	*out_value = (*(*buffer + 0) << 0u) |  // Byte 0
	             (*(*buffer + 1) << 8u) |  // Byte 1
	             (*(*buffer + 2) << 16u) | // Byte 2
	             (*(*buffer + 3) << 24u);  // Byte 3
	*buffer += 4;
}

static inline void
read_i16_rev(const uint8_t **buffer, int16_t *out_value)
{
	*out_value = (*(*buffer + 1) << 0u) | // Byte 1
	             (*(*buffer + 0) << 8u);  // Byte 2
	*buffer += 2;
}

static inline void
read_i15_to_i32(const uint8_t **buffer, int32_t *out_value)
{
	int16_t v = (*(*buffer + 0) << 0u) | // Byte 0
	            (*(*buffer + 1) << 8u);  // Byte 1
	v = (v ^ 0x8000);                    // Flip sign bit
	*out_value = v;                      // Properly sign extend.
	*buffer += 2;
}

static inline void
read_i32_rev(const uint8_t **buffer, int32_t *out_value)
{
	*out_value = (*(*buffer + 3) << 0u) |  // Byte 3
	             (*(*buffer + 2) << 8u) |  // Byte 2
	             (*(*buffer + 1) << 16u) | // Byte 1
	             (*(*buffer + 0) << 24u);  // Byte 0
	*buffer += 4;
}
```

X's packed struct (same layout) and its read path (header check, timestamp unit, temperature):

<!-- verbatim: X interface_lib/include/device_imu.h L68-L89 -->
xrealAirLinuxDriver `interface_lib/include/device_imu.h`, lines 68-89:
```c
struct __attribute__((__packed__)) device_imu_packet_t {
	uint8_t signature [2];
	uint8_t temperature [2];
	uint64_t timestamp;
	uint8_t angular_multiplier [2];
	uint8_t angular_divisor [4];
	uint8_t angular_velocity_x [3];
	uint8_t angular_velocity_y [3];
	uint8_t angular_velocity_z [3];
	uint8_t acceleration_multiplier [2];
	uint8_t acceleration_divisor [4];
	uint8_t acceleration_x [3];
	uint8_t acceleration_y [3];
	uint8_t acceleration_z [3];
	uint8_t magnetic_multiplier [2];
	uint8_t magnetic_divisor [4];
	uint8_t magnetic_x [2];
	uint8_t magnetic_y [2];
	uint8_t magnetic_z [2];
	uint32_t checksum;
	uint8_t _padding [6];
};
```

<!-- verbatim: X interface_lib/src/device_imu.c L1097-L1122 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 1097-1122:
```c
	if (sizeof(device_imu_packet_type) != transferred) {
		device_imu_error("Unexpected packet size");
		return DEVICE_IMU_ERROR_UNEXPECTED;
	}
	
	const uint64_t timestamp = le64toh(packet.timestamp);
	
	if ((packet.signature[0] == 0xaa) && (packet.signature[1] == 0x53)) {
		device_imu_callback(device, timestamp, DEVICE_IMU_EVENT_INIT);
		return DEVICE_IMU_ERROR_NO_ERROR;
	}
	
	if ((packet.signature[0] != 0x01) || (packet.signature[1] != 0x02)) {
		device_imu_error("Not matching signature");
		return DEVICE_IMU_ERROR_WRONG_SIGNATURE;
	}
	
	const uint64_t delta = timestamp - device->last_timestamp;
	const float deltaTime = (float) ((double) delta / 1e9);
	
	device->last_timestamp = timestamp;
	
	int16_t temperature = pack16bit_signed(packet.temperature);
	
	// According to the ICM-42688-P datasheet: (offset: 25 °C, sensitivity: 132.48 LSB/°C)
	device->temperature = ((float) temperature) / 132.48f + 25.0f;
```

R's parser (also the axis mapping, §7):

<!-- verbatim: R src/nreal_air.rs L454-L507 -->
ar-drivers-rs `src/nreal_air.rs`, lines 454-507:
```rust
    pub fn read_packet(&mut self) -> Result<GlassesEvent> {
        loop {
            let mut packet_data = [0u8; 0x80];
            let data_size = self.device.read_timeout(&mut packet_data, IMU_TIMEOUT)?;
            if data_size == 0 {
                return Err(Error::PacketTimeout);
            }

            if packet_data[0] == 1 && packet_data[1] == 2 {
                return self.parse_report(&packet_data);
            };
            // Else try again
        }
    }

    fn parse_report(&mut self, packet_data: &[u8]) -> Result<GlassesEvent> {
        // TODO: This skips over a 2 byte temperature field that may be useful.
        let mut reader = std::io::Cursor::new(&packet_data[4..]);

        let timestamp = reader.read_u64::<LittleEndian>()? / 1000;
        let gyro_mul = reader.read_u16::<LittleEndian>()? as f32;
        let gyro_div = reader.read_u32::<LittleEndian>()? as f32;
        let gyro_x = reader.read_i24::<LittleEndian>()? as f32;
        let gyro_y = reader.read_i24::<LittleEndian>()? as f32;
        let gyro_z = reader.read_i24::<LittleEndian>()? as f32;
        let gyroscope = Vector3::new(
            // The bias fields do not correspond to the raw fields, but for some reason
            // this looks like the correct zero.
            -(gyro_x * gyro_mul / gyro_div).to_radians() - self.gyro_bias.x,
            (gyro_z * gyro_mul / gyro_div).to_radians() + self.gyro_bias.y,
            (gyro_y * gyro_mul / gyro_div).to_radians() + self.gyro_bias.z,
        );

        let acc_mul = reader.read_u16::<LittleEndian>()? as f32;
        let acc_div = reader.read_u32::<LittleEndian>()? as f32;
        let acc_x = reader.read_i24::<LittleEndian>()? as f32;
        let acc_y = reader.read_i24::<LittleEndian>()? as f32;
        let acc_z = reader.read_i24::<LittleEndian>()? as f32;
        let accelerometer = Vector3::new(
            // The bias fields do not correspond to the raw fields, but for some reason
            // this looks like the correct zero.
            -(acc_x * acc_mul / acc_div) * 9.81 - self.accelerometer_bias.x,
            (acc_z * acc_mul / acc_div) * 9.81 + self.accelerometer_bias.y,
            (acc_y * acc_mul / acc_div) * 9.81 + self.accelerometer_bias.z,
        );
        // TODO: magnetometer. It's in the same format, but it's non-trivially
        //       rotated.
        // TODO: Check checksum
        Ok(GlassesEvent::AccGyro {
            accelerometer,
            gyroscope,
            timestamp,
        })
    }
```

### Layout (first 64 bytes; little endian unless noted)

| Offset | Size | Field | Encoding | Notes |
|---|---|---|---|---|
| `0x00` | 2 | header | `01 02` | M checks only byte 0 == 1; X and R require `01 02`. The bridge requires `01 02` and counts anything else |
| `0x02` | 2 | temperature | int16 | °C = raw ÷ 132.48 + 25 (M, X: "According to the ICM-42688-P datasheet") |
| `0x04` | 8 | timestamp | uint64 | **nanoseconds**: X divides deltas by 1e9, R divides by 1000 to get µs, V says "Timestamp (nanoseconds)" |
| `0x0C` | 2 | gyro multiplier | int16 | M, X read signed; R reads `u16` |
| `0x0E` | 4 | gyro divisor | int32 | M, X signed; R `u32` |
| `0x12` | 3+3+3 | gyro x, y, z | int24, two's complement | **deg/s** = raw × multiplier ÷ divisor (M converts deg → rad after this) |
| `0x1B` | 2 | accel multiplier | int16 | |
| `0x1D` | 4 | accel divisor | int32 | |
| `0x21` | 3+3+3 | accel x, y, z | int24 | **g** = raw × multiplier ÷ divisor (M: "Go from Gs to m/s2") |
| `0x2A` | 2 | mag multiplier | int16 **big endian** | M `read_i16_rev`, X `pack16bit_signed_swap`; V calls it "Magnetometer offset" |
| `0x2C` | 4 | mag divisor | int32 **big endian** | M `read_i32_rev`, X `pack32bit_signed_swap` |
| `0x30` | 2+2+2 | mag x, y, z | uint16 LE with the top bit flipped (value = raw16 − 0x8000) | unit undocumented; R ignores the magnetometer |
| `0x36` | 4 | "checksum" | — | M skips it, R: `// TODO: Check checksum`. Algorithm unknown; not checked |
| `0x3A` | 6 | unknown | — | M: "Unknown, skip 6 bytes." |

Ultra: M accepts a sensor report only when its length equals `max_size` (512), and X reads just the
first 64 bytes of each report (hidapi returns at most the length asked for). The bridge reads up to
512 bytes and parses the first 64, so either report length works.

Unit and wording notes:

<!-- quote: voidcomputing-dl/wbp.txt -->
> 0x04 8 Timestamp (nanoseconds)

<!-- quote: voidcomputing-dl/wbp.txt -->
> Yes, there are 3 byte signed integers there. They are encoded the same way as "regular" 4 byte integers (little endian, one's complement), but on 3 bytes.

The code in M and X sign-extends (two's complement), and R uses `read_i24`; the bridge follows the
code. M's header also defines `XREAL_AIR_TICKS_PER_SECOND (1000.0) // 1 KHz ticks` (`xreal_air_hmd.h`
line 63), but nothing in the driver uses it, and its own timestamp handling treats the delta as
nanoseconds (`time_duration_ns inter_sample_duration_ns = delta;` in `handle_sensor_msg` above). The
bridge uses nanoseconds and warns when the device clock disagrees with the host clock.

<!-- quote: voidcomputing-dl/wbp.txt -->
> 0x02 2 Temperature (raw data from the ICM-20602)

(V names an ICM-20602 for the original Air; M and X use the ICM-42688-P formula. Temperature is display-only.)

## 6. Sample rate

No source measures it. X configures its fusion for 1000 Hz:

<!-- verbatim: X interface_lib/src/device_imu.c L495-L512 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 495-512:
```c
	const uint32_t SAMPLE_RATE = 1000;
	
	device->offset = malloc(sizeof(FusionOffset));
	device->ahrs = malloc(sizeof(FusionAhrs));
	
	if (device->offset) {
		FusionOffsetInitialise((FusionOffset*) device->offset, SAMPLE_RATE);
	}

	FusionAhrsInitialise((FusionAhrs*) device->ahrs);
	
	const FusionAhrsSettings settings = {
			.convention = FusionConventionNed,
			.gain = 0.5f,
			.accelerationRejection = 10.0f,
			.magneticRejection = 20.0f,
			.recoveryTriggerPeriod = 5 * SAMPLE_RATE, /* 5 seconds */
	};
```

B's README says its loop runs at 1000 Hz on macOS, which is about its own loop, not the sensor:

<!-- quote: bevy-demo-dl/README.md -->
> Might want to consider using `async-hid` instead of `hidapi` for the driver as a way to fix the jitter, although the loop is already running at 1000 Hz so I'm not too sure if that would help.

The Ultra's rate is **UNVERIFIED**. The bridge prints both the host packet rate and the rate implied by
the device timestamps every 5 s.

## 7. Axes

R documents its output frame:

<!-- verbatim: R src/lib.rs L113-L132 -->
ar-drivers-rs `src/lib.rs`, lines 113-132:
```rust
/// AR glasses sensor event, got from [`ARGlasses::read_event`]
///
/// Coordinate system is "RUB": Positive X is Right, Positive Y is Up, Positive Z is backwards.
/// This is the same as the Android sensor coordinate system.
#[derive(Debug, Clone)]
pub enum GlassesEvent {
    /// Synchronized accelerometer and gyroscope data.
    AccGyro {
        /// Accelerometer data in m^2/s.
        ///
        /// Remember that while gravitational acceleration is "down", the acceleration
        /// the device "feels" is the one opposite from that, so the normal reading
        /// when the device is upright is (0, 9.81, 0)
        accelerometer: Vector3<f32>,
        /// Gyroscope data. Right handed rotation in rad/sec,
        /// e.g. turning left is positive y axis.
        gyroscope: Vector3<f32>,
        /// Timestamp, in device time, in microseconds
        timestamp: u64,
    },
```

R's `parse_report` (§5) maps device `(x, y, z)` to `(-x, z, y)` for both gyro and accel. M does the same
in two steps (pre: `(-x, -z, -y)`, then post: `(x, -y, -z)`, giving `(-x, z, y)`):

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L218-L233 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 218-233:
```c
static void
pre_biased_coordinate_system(struct xrt_vec3 *out)
{
	const float y = out->y;
	out->x = -out->x;
	out->y = -out->z;
	out->z = -y;
}

static void
post_biased_coordinate_system(const struct xrt_vec3 *in, struct xrt_vec3 *out)
{
	out->x = in->x;
	out->y = -in->y;
	out->z = -in->z;
}
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L242-L244 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 242-244:
```c
	const float accel_factor = (float)sample->accel_multiplier / (float)sample->accel_divisor;
	const float gyro_factor = (float)sample->gyro_multiplier / (float)sample->gyro_divisor;
	const float mag_factor = (float)sample->mag_multiplier / (float)sample->mag_divisor;
```

<!-- verbatim: M src/xrt/drivers/xreal_air/xreal_air_hmd.c L275-L279 -->
Monado `src/xrt/drivers/xreal_air/xreal_air_hmd.c`, lines 275-279:
```c
	// Go from Gs to m/s2.
	math_vec3_scalar_mul((float)+MATH_GRAVITY_M_S2, &accel);

	// Got from angles to radians.
	math_vec3_scalar_mul((float)+M_PI / 180.0f, &gyro);
```

M's 3DoF fusion treats +Y as up (so a level, still device must read +Y after the mapping):

<!-- verbatim: M src/xrt/auxiliary/math/m_imu_3dof.c L161-L171 -->
Monado `src/xrt/auxiliary/math/m_imu_3dof.c`, lines 161-171:
```c
			struct xrt_vec3 tilt = {
			    accel_mean.z,
			    0,
			    -accel_mean.x,
			};

			tilt = m_vec3_normalize(tilt);
			accel_mean = m_vec3_normalize(accel_mean);

			struct xrt_vec3 up = {0, 1.0f, 0};
			float tilt_angle = m_vec3_angle(up, accel_mean);
```

X uses Fusion's North-East-Down convention (`.convention = FusionConventionNed`, §6) with:

<!-- verbatim: X interface_lib/src/device_imu.c L789-L795 -->
xrealAirLinuxDriver `interface_lib/src/device_imu.c`, lines 789-795:
```c
static void pre_biased_coordinate_system(FusionVector* v) {
	*v = FusionAxesSwap(*v, FusionAxesAlignmentNXNZNY);
}

static void post_biased_coordinate_system(const FusionVector* v, FusionVector* res) {
	*res = FusionAxesSwap(*v, FusionAxesAlignmentPZPXPY);
}
```

<!-- verbatim: F Fusion/FusionAxes.h L95-L99 -->
Fusion `Fusion/FusionAxes.h`, lines 95-99:
```c
        case FusionAxesAlignmentNXNZNY:
            result.axis.x = -sensor.axis.x;
            result.axis.y = -sensor.axis.z;
            result.axis.z = -sensor.axis.y;
            return result;
```

<!-- verbatim: F Fusion/FusionAxes.h L145-L149 -->
Fusion `Fusion/FusionAxes.h`, lines 145-149:
```c
        case FusionAxesAlignmentPZPXPY:
            result.axis.x = +sensor.axis.z;
            result.axis.y = +sensor.axis.x;
            result.axis.z = +sensor.axis.y;
            return result;
```

So X maps device `(x, y, z)` → `(-x, -z, -y)` → `(-y, -x, -z)` = (forward, right, down) in NED, which
agrees with R and M. **Derived device axes (worn glasses): +X = left, +Y = back (toward the face),
+Z = up.** The device frame is right-handed (left × back = up). A level, still device reads accel ≈
(0, 0, +1 g). The bridge's `device_to_rub(v) = (-v.x, v.z, v.y)` (RUB: +X right, +Y up, +Z back).

The calibration JSON on the glasses (`0x14`/`0x15`, `accel_bias`, `gyro_bias`, misalignment quaternions)
is in yet another frame (V: "the coordinate system of the raw sensor readings is different from the
calibration file's coordinate system"). The bridge does not read it; it estimates the gyro bias itself (§9).

## 8. Bridge output frame

**Wire format (pinned).** One UDP datagram to `127.0.0.1:<port>` (default 47800) = one UTF-8 JSON object
`{"t": <seconds since the bridge started, float>, "q": [x, y, z, w]}`. The bridge appends `"seq"` (a
counter) and `"src"` (`"imu"` or `"fake"`) after `t` and `q`; receivers ignore them. A datagram captured
from a `--fake` run: `{"t":2.996300,"q":[-0.0160335,0.2056157,-0.0462472,0.9774080],"seq":749,"src":"fake"}`.

**Meaning of `q`.** A unit quaternion in Unity's frame (left-handed: +X right, +Y up, +Z forward) that
rotates head-local vectors into a gravity-aligned world frame. Identity = the glasses are level and face
the way they faced when the bridge started. Pitch and roll are absolute (gravity); yaw is relative to the
start heading. Unity applies it as `new Quaternion(x, y, z, w)` with no further conversion.

**Derivation.** The fusion runs in RUB (§7), right-handed: head frame H = RUB, world W = RUB axes with +Y
up and heading 0 at start, `q_rub` takes H to W. Unity's frame is RUB with Z mirrored, `S = diag(1, 1, -1)`,
so a rotation matrix becomes `R_unity = S · R_rub · S`. A reflection reverses the sense of rotation: a
rotation by θ about unit axis `a` becomes a rotation by −θ about `S·a`, whose quaternion is
`(−sin(θ/2)·S·a, cos(θ/2))`. Therefore

    q_unity = (−x_rub, −y_rub, +z_rub, w_rub)        (xreal_imu.rub_to_unity_quat)
    device vector (x, y, z)  →  Unity vector (−x, +z, −y)

Unity turns `q` into a rotation with the standard formula (the same one `xreal_imu.q_rotate` uses), so
`S·R_rub·S` is exactly what Unity applies:

<!-- verbatim: U Runtime/Export/Math/Quaternion.cs L97-L117 -->
UnityCsReference 2021.3 `Runtime/Export/Math/Quaternion.cs`, lines 97-117:
```csharp
        public static Vector3 operator*(Quaternion rotation, Vector3 point)
        {
            float x = rotation.x * 2F;
            float y = rotation.y * 2F;
            float z = rotation.z * 2F;
            float xx = rotation.x * x;
            float yy = rotation.y * y;
            float zz = rotation.z * z;
            float xy = rotation.x * y;
            float xz = rotation.x * z;
            float yz = rotation.y * z;
            float wx = rotation.w * x;
            float wy = rotation.w * y;
            float wz = rotation.w * z;

            Vector3 res;
            res.x = (1F - (yy + zz)) * point.x + (xy - wz) * point.y + (xz + wy) * point.z;
            res.y = (xy + wz) * point.x + (1F - (xx + zz)) * point.y + (yz - wx) * point.z;
            res.z = (xz - wy) * point.x + (yz + wx) * point.y + (1F - (xx + yy)) * point.z;
            return res;
        }
```

Unity's Euler angles (used only for the human test below):

<!-- quote: unity-dl/Quaternion.Euler.txt -->
> Returns a rotation that rotates z degrees around the z axis, x degrees around the x axis, and y degrees around the y axis; applied in that order.

From that formula, a positive rotation about Unity +Y turns `Vector3.forward` toward +X (right), about
+X tilts it down, and about +Z lifts `Vector3.right` up.

**Human test** (wear the glasses, start `bridge.py --watch`, face forward, hold each pose ~1 s; the
watch line prints Unity's signed Euler angles; the Inspector shows negatives as 360 − |angle|):

| Motion | Expected Unity Euler sign | Vector check |
|---|---|---|
| Nod down (look at the floor) | **x > 0** (look up: x < 0) | `q * Vector3.forward` has y < 0 |
| Turn head left | **y < 0** (right: y > 0) | 90° left: `q * Vector3.forward` ≈ (−1, 0, 0) |
| Tilt head left (left ear toward left shoulder) | **z > 0** (right: z < 0) | `q * Vector3.right` has y > 0 |
| Back to the start pose | x, y, z ≈ 0 | `q` ≈ (0, 0, 0, 1) |

If a sign is wrong, the device axes differ on the Ultra (§1). Report which row failed; the fix is one
line in `device_to_rub` (do not flip signs in Unity).

## 9. Bridge fusion (bridge parameters, not protocol facts)

- **Start-up gyro bias:** mean gyro over 1.5 s of stillness (in the window every gyro axis stays within
  ±5 °/s and spans < 2 °/s, and |a| spans < 0.05 g; otherwise the window restarts; after 15 s it starts
  anyway and says FORCED, using the current still window if there is one, else zero bias). The mean accel of that window sets the initial pitch/roll; heading starts at 0. No UDP is
  sent until then (Unity stays headLocked meanwhile).
- **Run-time gyro offset:** a time-based port of F's `FusionOffset` (X runs it on these glasses at
  1000 Hz). `--no-bias-tracking` turns it off:

<!-- verbatim: F Fusion/FusionOffset.c L17-L30 -->
Fusion `Fusion/FusionOffset.c`, lines 17-30:
```c
/**
 * @brief Cutoff frequency in Hz.
 */
#define CUTOFF_FREQUENCY (0.02f)

/**
 * @brief Timeout in seconds.
 */
#define TIMEOUT (5)

/**
 * @brief Threshold in degrees per second.
 */
#define THRESHOLD (3.0f)
```

<!-- verbatim: F Fusion/FusionOffset.c L54-L74 -->
Fusion `Fusion/FusionOffset.c`, lines 54-74:
```c
FusionVector FusionOffsetUpdate(FusionOffset *const offset, FusionVector gyroscope) {

    // Subtract offset from gyroscope measurement
    gyroscope = FusionVectorSubtract(gyroscope, offset->gyroscopeOffset);

    // Reset timer if gyroscope not stationary
    if ((fabsf(gyroscope.axis.x) > THRESHOLD) || (fabsf(gyroscope.axis.y) > THRESHOLD) || (fabsf(gyroscope.axis.z) > THRESHOLD)) {
        offset->timer = 0;
        return gyroscope;
    }

    // Increment timer while gyroscope stationary
    if (offset->timer < offset->timeout) {
        offset->timer++;
        return gyroscope;
    }

    // Adjust offset if timer has elapsed
    offset->gyroscopeOffset = FusionVectorAdd(offset->gyroscopeOffset, FusionVectorMultiplyScalar(gyroscope, offset->filterCoefficient));
    return gyroscope;
}
```

- **Orientation:** 6-axis Mahony complementary filter in RUB, `kp = 0.5` rad/s per unit error (tilt time
  constant ≈ 2 s; `--kp`), `ki = 0`, gravity correction skipped while | |a| − 1 g | > 0.1 g. dt comes from
  the device timestamps (`--dt-source host` uses arrival times instead); gaps over 0.1 s are skipped.
- **Output:** at most 250 datagrams/s (`--send-hz`, 0 = every IMU sample).
- **Status every 5 s:** IMU rate (host and device clock), UDP rate, yaw drift in °/min over the window
  with the share of still samples (< 90 % still = moving, not a drift number), current Euler angles,
  temperature.

## 10. macOS notes

<!-- quote: bevy-demo-dl/README.md -->
> Should work with XREAL Air, Air 2, and Air 2 Pro. Also probably only works on Mac OS (Windows doesn't work, see [issue](https://github.com/badicsalex/ar-drivers-rs/issues/13)).

<!-- quote: gitlab-api/mr3090_description.txt -->
> Xreal Air already built on Windows and macOS, it should now get its HID interfaces there.

M on macOS is one day old and untested for XREAL ("should"). How it picks an interface with hidapi (the
same rule the bridge uses):

<!-- verbatim: M src/xrt/state_trackers/prober/p_prober.c L1257-L1289 -->
Monado `src/xrt/state_trackers/prober/p_prober.c`, lines 1257-1289:
```c
#elif defined(XRT_HAVE_HIDAPI)
	unsigned char serial[256] = {0};
	bool have_serial =
	    p_get_string_descriptor(xp, xpdev, XRT_PROBER_STRING_SERIAL_NUMBER, serial, sizeof(serial)) > 0;

	struct hid_device_info *devs = hid_enumerate(pdev->base.vendor_id, pdev->base.product_id);
	ret = -1;

	for (struct hid_device_info *cur = devs; cur != NULL; cur = cur->next) {
		int iface = cur->interface_number >= 0 ? cur->interface_number : 0;
		if (iface != hid_iface) {
			continue;
		}

		bool have_hid_serial = cur->serial_number != NULL && cur->serial_number[0] != L'\0';
		if (have_serial && have_hid_serial && !hidapi_serial_equals(cur->serial_number, (const char *)serial)) {
			continue;
		}

		// An interface is listed once per top-level collection, try the next.
		ret = h_hid_open_hidapi(cur->path, out_hid_dev);
		if (ret == 0) {
			break;
		}
		U_LOG_D("Failed to open device '%s' got '%i'", cur->path, ret);
	}

	hid_free_enumeration(devs);

	if (ret != 0) {
		U_LOG_E("Could not open the requested hid interface (%i) on the device!", hid_iface);
	}
	return ret;
```

hidapi opens devices exclusively on macOS by default: quit any other program that has the glasses open
(details in `docs/apis/python/hidapi.md`).

## 11. Test vectors and what M-S2 must confirm

**SYNTHETIC** packet, built field by field from the §5 table by a scratch script (not captured from
glasses; the multiplier and divisor values are arbitrary test values, not device constants).
`test_xreal_imu.py` copies these bytes and checks that they still match this block:

<!-- test-bytes: imu_packet_example -->
```
01 02 2D 05 00 CA 9A 3B 00 00 00 00 01 00 E8 03
00 00 DC 05 00 3C F6 FF 00 00 00 01 00 00 20 00
00 00 00 00 00 F0 FF 00 20 00 00 01 00 00 00 64
FA 80 83 7F 00 80 00 00 00 00 00 00 00 00 00 00
```

| Field | Raw | Decoded |
|---|---|---|
| header | `01 02` | IMU packet |
| temperature | 1325 | 1325 ÷ 132.48 + 25 = 35.00 °C |
| timestamp | 1 000 000 000 | 1.0 s |
| gyro multiplier / divisor | 1 / 1000 | |
| gyro x, y, z | 1500, −2500, 0 | 1.5, −2.5, 0.0 °/s |
| accel multiplier / divisor | 1 / 8192 | |
| accel x, y, z | 0, −4096, 8192 | 0.0, −0.5, 1.0 g |
| mag multiplier / divisor (big endian) | 1 / 100 | |
| mag x, y, z (stored `FA 80`, `83 7F`, `00 80`) | 250, −125, 0 | 2.5, −1.25, 0.0 (unit unknown) |
| checksum field | 0 | not checked |

An Air 2 Ultra report is the same 64 bytes followed by 448 more (zeros in the test).

**M-S2 must confirm on the Ultra** (all UNVERIFIED today): (1) `--list` shows pid `0x0426` with an
interface 2; (2) after the start command, reports begin `01 02`; (3) the printed rate (expected ≈ 1000 Hz)
and that the device clock agrees with the host clock (ns timestamps); (4) the §8 human test signs;
(5) yaw drift < 2 °/min while still. Paste the first `--dump 3` lines into this section as a real capture.
