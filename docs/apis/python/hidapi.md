# hidapi for Python: the calls `tools/imu-bridge` uses

*Person 1 · Sat 2026-11-14 ~01:40 PT · for spike M-S2 / slice SL8. The protocol itself is in
`docs/apis/glasses/imu-protocol.md`.*

## Version

| | |
|---|---|
| Package | PyPI `hidapi` **0.15.0** (module name `hid`), the Cython binding from https://github.com/trezor/cython-hidapi |
| Installed here | `python3 -m pip install --user hidapi` → `hidapi-0.15.0-cp311-cp311-macosx_11_0_arm64.whl` (Python 3.11.5, Apple silicon), 2026-11-14 |
| Binding source read (C) | tag `0.15.0` = commit `0b5671548f20d4e2cb4bbb95b7821839da5de146`: `hid.pyx`, `chid.pxd` |
| C library inside | libusb/hidapi **0.15.0**, the submodule commit `d6b2a974608dec3b76fb1e36c189f22b9cf3650c` (`VERSION` file: `0.15.0`); `python3 -c 'import hid; print(hid.version_str())'` prints `0.15.0` here |
| C sources read (H) | `hidapi/hidapi.h`, `mac/hid.c`, `mac/hidapi_darwin.h` at that commit: https://github.com/libusb/hidapi/tree/d6b2a974608dec3b76fb1e36c189f22b9cf3650c |

Excerpts are byte-for-byte copies of the stated lines (`<!-- verbatim -->` markers let a script
re-check them).

## Calls the bridge uses

| Python call | C call | Used for |
|---|---|---|
| `hid.enumerate(vendor_id, product_id)` | `hid_enumerate` | `--list`; finding the IMU interface by `interface_number` |
| `hid.device()` then `.open_path(path)` | `hid_open_path` | opening the IMU interface (`path` is `bytes`) |
| `.read(max_length, timeout_ms)` | `hid_read_timeout` (timeout > 0) / `hid_read` | reading reports (timed, 20 ms) |
| `.write(data)` | `hid_write` | the `0x19` start/stop commands |
| `.error()` | `hid_error` | the reason after a failed open or write |
| `.close()` | `hid_close` | Ctrl+C / exit |

Not used, listed because the brief asks: `.set_nonblocking(v)`, `.send_feature_report(buf)`,
`.get_feature_report(report_num, max_length)`. The XREAL protocol uses plain input/output reports
(Monado, xrealAirLinuxDriver and ar-drivers-rs all use `hid_read`/`hid_write`), not feature reports.

### `hid.enumerate`

<!-- verbatim: C hid.pyx L31-L75 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 31-75:
```python
def enumerate(int vendor_id=0, int product_id=0):
    """Return a list of discovered HID devices.

    The fields of dict are:

     - 'path'
     - 'vendor_id'
     - 'product_id'
     - 'serial_number'
     - 'release_number'
     - 'manufacturer_string'
     - 'product_string'
     - 'usage_page'
     - 'usage'
     - 'interface_number'

    :param vendor_id: Vendor id to look for, default = 0
    :type vendor_id: int, optional
    :param product_id: Product id to look for, default = 0
    :type product_id: int, optional
    :return: List of device dictionaries
    :rtype: List[Dict]
    """
    cdef hid_device_info* info
    with nogil:
        info = hid_enumerate(vendor_id, product_id)
    cdef hid_device_info* c = info
    res = []
    while c:
        res.append({
            'path': c.path,
            'vendor_id': c.vendor_id,
            'product_id': c.product_id,
            'serial_number': U(c.serial_number),
            'release_number': c.release_number,
            'manufacturer_string': U(c.manufacturer_string),
            'product_string': U(c.product_string),
            'usage_page': c.usage_page,
            'usage': c.usage,
            'interface_number': c.interface_number,
            'bus_type': c.bus_type,
        })
        c = c.next
    hid_free_enumeration(info)
    return res
```

<!-- verbatim: C chid.pxd L16-L28 -->
cython-hidapi 0.15.0 `chid.pxd`, lines 16-28:
```python
    cdef struct hid_device_info:
        char *path
        unsigned short vendor_id
        unsigned short product_id
        wchar_t *serial_number
        unsigned short release_number
        wchar_t *manufacturer_string
        wchar_t *product_string
        unsigned short usage_page
        unsigned short usage
        int interface_number
        hid_device_info *next
        hid_bus_type bus_type
```

<!-- verbatim: H hidapi/hidapi.h L166-L178 -->
libusb/hidapi 0.15.0 `hidapi/hidapi.h`, lines 166-178:
```c
			/** Usage Page for this Device/Interface
			    (Windows/Mac/hidraw only) */
			unsigned short usage_page;
			/** Usage for this Device/Interface
			    (Windows/Mac/hidraw only) */
			unsigned short usage;
			/** The USB interface which this logical device
			    represents.

			    Valid only if the device is a USB HID device.
			    Set to -1 in all other cases.
			*/
			int interface_number;
```

### `device.open_path` and `device.close`

<!-- verbatim: C hid.pyx L146-L169 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 146-169:
```python
    def open_path(self, bytes path):
        """Open connection by path.

        :param path: Path to device
        :type path: bytes
        :raises IOError:
        """
        cdef char* cbuff = path
        if self._c_hid != NULL:
            raise RuntimeError('already open')
        self._c_hid = hid_open_path(cbuff)
        if self._c_hid == NULL:
            raise IOError('open failed')
        self._close = weakref.finalize(self, _closer.wrap(self._c_hid).close)

    def close(self):
        """Close connection.

        This should always be called after opening a connection.
        """
        if self._c_hid != NULL:
            self._close()
            self._close.detach()
            self._c_hid = NULL
```

### `device.read` (timed and blocking)

<!-- verbatim: C hid.pyx L206-L240 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 206-240:
```python
    def read(self, int max_length, int timeout_ms=0):
        """Return a list of integers (0-255) from the device up to max_length bytes.

        :param max_length: Maximum number of bytes to read
        :type max_length: int
        :param timeout_ms: Number of milliseconds until timeout (default: no timeout)
        :type timeout_ms: int, optional
        :return: Read bytes
        :rtype: List[int]
        """
        if self._c_hid == NULL:
            raise ValueError('not open')
        cdef unsigned char lbuff[16]
        cdef unsigned char* cbuff
        cdef size_t c_max_length = max_length
        cdef int c_timeout_ms = timeout_ms
        cdef hid_device * c_hid = self._c_hid
        try:
            if max_length <= 16:
                cbuff = lbuff
            else:
                cbuff = <unsigned char *>malloc(max_length)
            if timeout_ms > 0:
                with nogil:
                    n = hid_read_timeout(c_hid, cbuff, c_max_length, c_timeout_ms)
            else:
                with nogil:
                    n = hid_read(c_hid, cbuff, c_max_length)
            if n == -1:
                raise IOError('read error')
            res = list(cbuff[:n])
        finally:
            if max_length > 16:
                free(cbuff)
        return res
```

So `read(n, timeout_ms)` with `timeout_ms > 0` waits at most that long and returns `[]` on a timeout;
`timeout_ms = 0` (the default) calls `hid_read`, which blocks unless `set_nonblocking(1)` was called.
It raises `IOError('read error')` when the C call returns −1, and returns at most `max_length` bytes.

<!-- verbatim: H hidapi/hidapi.h L327-L350 -->
libusb/hidapi 0.15.0 `hidapi/hidapi.h`, lines 327-350:
```c
		/** @brief Read an Input report from a HID device with timeout.

			Input reports are returned
			to the host through the INTERRUPT IN endpoint. The first byte will
			contain the Report number if the device uses numbered reports.

			@ingroup API
			@param dev A device handle returned from hid_open().
			@param data A buffer to put the read data into.
			@param length The number of bytes to read. For devices with
				multiple reports, make sure to read an extra byte for
				the report number.
			@param milliseconds timeout in milliseconds or -1 for blocking wait.

			@returns
				This function returns the actual number of bytes read and
				-1 on error.
				Call hid_read_error(dev) to get the failure reason.
				If no packet was available to be read within
				the timeout period, this function returns 0.

			@note This function doesn't change the buffer returned by the hid_error(dev).
		*/
		int HID_API_EXPORT HID_API_CALL hid_read_timeout(hid_device *dev, unsigned char *data, size_t length, int milliseconds);
```

### `device.write`

<!-- verbatim: C hid.pyx L171-L192 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 171-192:
```python
    def write(self, buff):
        """Accept a list of integers (0-255) and send them to the device.

        :param buff: Data to write (must be convertible to `bytes`)
        :type buff: Any
        :return: Write result
        :rtype: int
        """
        if self._c_hid == NULL:
            raise ValueError('not open')
        # convert to bytes
        if sys.version_info < (3, 0):
            buff = ''.join(map(chr, buff))
        else:
            buff = bytes(buff)
        cdef hid_device * c_hid = self._c_hid
        cdef unsigned char* cbuff = buff # covert to c string
        cdef size_t c_buff_len = len(buff)
        cdef int result
        with nogil:
            result = hid_write(c_hid, cbuff, c_buff_len)
        return result
```

It returns the C result and does **not** raise: −1 means failure (then read `device.error()`).

<!-- verbatim: H hidapi/hidapi.h L298-L325 -->
libusb/hidapi 0.15.0 `hidapi/hidapi.h`, lines 298-325:
```c
		/** @brief Write an Output report to a HID device.

			The first byte of @p data[] must contain the Report ID. For
			devices which only support a single report, this must be set
			to 0x0. The remaining bytes contain the report data. Since
			the Report ID is mandatory, calls to hid_write() will always
			contain one more byte than the report contains. For example,
			if a hid report is 16 bytes long, 17 bytes must be passed to
			hid_write(), the Report ID (or 0x0, for devices with a
			single report), followed by the report data (16 bytes). In
			this example, the length passed in would be 17.

			hid_write() will send the data on the first interrupt OUT 
			endpoint, if one exists. If it does not the behaviour is as 
			@ref hid_send_output_report

			@ingroup API
			@param dev A device handle returned from hid_open().
			@param data The data to send, including the report number as
				the first byte.
			@param length The length in bytes of the data to send.

			@returns
				This function returns the actual number of bytes written and
				-1 on error.
				Call hid_error(dev) to get the failure reason.
		*/
		int  HID_API_EXPORT HID_API_CALL hid_write(hid_device *dev, const unsigned char *data, size_t length);
```

The XREAL commands start with `0xAA`, not a report id `0x00`; Monado, xrealAirLinuxDriver and
ar-drivers-rs (except on Windows) all write them like that. What macOS hidapi does with the first byte:

<!-- verbatim: H mac/hid.c L1101-L1141 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 1101-1141:
```c
static int set_report(hid_device *dev, IOHIDReportType type, const unsigned char *data, size_t length)
{
	const unsigned char *data_to_send = data;
	CFIndex length_to_send = length;
	IOReturn res;
	unsigned char report_id;

	if (!data || (length == 0)) {
		register_device_error(dev, "Zero buffer/length");
		return -1;
	}

	register_device_error(dev, NULL);

	report_id = data[0];

	if (report_id == 0x0) {
		/* Not using numbered Reports.
		   Don't send the report number. */
		data_to_send = data+1;
		length_to_send = length-1;
	}

	/* Avoid crash if the device has been unplugged. */
	if (dev->disconnected) {
		register_device_error(dev, "Device is disconnected");
		return -1;
	}

	res = IOHIDDeviceSetReport(dev->device_handle,
	                           type,
	                           report_id,
	                           data_to_send, length_to_send);

	if (res != kIOReturnSuccess) {
		register_device_error_format(dev, "IOHIDDeviceSetReport failed: (0x%08X) %s", res, mach_error_string(res));
		return -1;
	}

	return (int) length;
}
```

So on macOS a buffer starting `0xAA` is sent whole, with `0xAA` passed as the report id, and a buffer
starting `0x00` is sent without that byte (report id 0). The bridge's fallback forms (`imu-protocol.md`
§3) use the second case.

### `device.set_nonblocking`, feature reports, `device.error`

<!-- verbatim: C hid.pyx L194-L204 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 194-204:
```python
    def set_nonblocking(self, int v):
        """Set the nonblocking flag.

        :param v: Flag value (1 or 0, True or False)
        :type v: int, bool
        :return: Flag result
        :rtype: int
        """
        if self._c_hid == NULL:
            raise ValueError('not open')
        return hid_set_nonblocking(self._c_hid, v)
```

<!-- verbatim: C hid.pyx L308-L329 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 308-329:
```python
    def send_feature_report(self, buff):
        """Accept a list of integers (0-255) and send them to the device.

        :param buff: Data to send (must be convertible into bytes)
        :type buff: any
        :return: Send result
        :rtype: int
        """
        if self._c_hid == NULL:
            raise ValueError('not open')
        # convert to bytes
        if sys.version_info < (3, 0):
            buff = ''.join(map(chr, buff))
        else:
            buff = bytes(buff)
        cdef hid_device * c_hid = self._c_hid
        cdef unsigned char* cbuff = buff  # covert to c string
        cdef size_t c_buff_len = len(buff)
        cdef int result
        with nogil:
            result = hid_send_feature_report(c_hid, cbuff, c_buff_len)
        return result
```

<!-- verbatim: C hid.pyx L351-L384 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 351-384:
```python
    def get_feature_report(self, int report_num, int max_length):
        """Receive feature report.

        :param report_num:
        :type report_num: int
        :param max_length:
        :type max_length: int
        :return: Incoming feature report
        :rtype: List[int]
        :raises ValueError: If connection is not opened.
        :raises IOError:
        """
        if self._c_hid == NULL:
            raise ValueError('not open')
        cdef hid_device * c_hid = self._c_hid
        cdef unsigned char lbuff[16]
        cdef unsigned char* cbuff
        cdef size_t c_max_length = max_length
        cdef int n
        try:
            if max_length <= 16:
                cbuff = lbuff
            else:
                cbuff = <unsigned char *>malloc(max_length)
            cbuff[0] = report_num
            with nogil:
                n = hid_get_feature_report(c_hid, cbuff, c_max_length)
            if n < 0:
                raise IOError('read error')
            res = list(cbuff[:n])
        finally:
            if max_length > 16:
                free(cbuff)
        return res
```

<!-- verbatim: C hid.pyx L421-L428 -->
cython-hidapi 0.15.0 `hid.pyx`, lines 421-428:
```python
    def error(self):
        """Get error from device, or global error if no device is opened.

        :return:
        :rtype: str
        :raises IOError:
        """
        return U(<wchar_t*>hid_error(self._c_hid))
```

`error()` returns `hid_error()`. hidapi 0.15.0 reports read failures through a separate
`hid_read_error()` ("This function doesn't change the buffer returned by the hid_error(dev)", in the
`hid_read_timeout` excerpt above), which the binding does not expose (it is not in `chid.pxd`), so
`error()` will not explain a failed read.

<!-- verbatim: C chid.pxd L30-L52 -->
cython-hidapi 0.15.0 `chid.pxd`, lines 30-52:
```python
    hid_device_info* hid_enumerate(unsigned short, unsigned short) nogil
    void hid_free_enumeration(hid_device_info*)

    hid_device* hid_open(unsigned short, unsigned short, const wchar_t*)
    hid_device* hid_open_path(char *path)
    void hid_close(hid_device *)
    int hid_init()
    int hid_exit()
    int hid_write(hid_device* device, unsigned char *data, int length) nogil
    int hid_read(hid_device* device, unsigned char* data, int max_length) nogil
    int hid_read_timeout(hid_device* device, unsigned char* data, int max_length, int milliseconds) nogil
    int hid_set_nonblocking(hid_device* device, int value)
    int hid_get_report_descriptor(hid_device* device, unsigned char *data, int length) nogil
    int hid_send_feature_report(hid_device* device, unsigned char *data, int length) nogil
    int hid_get_feature_report(hid_device* device, unsigned char *data, int length) nogil
    int hid_get_input_report(hid_device* device, unsigned char *data, int length) nogil

    int hid_get_manufacturer_string(hid_device*, wchar_t *, size_t)
    int hid_get_product_string(hid_device*, wchar_t *, size_t)
    int hid_get_serial_number_string(hid_device*, wchar_t *, size_t)
    int hid_get_indexed_string(hid_device*, int, wchar_t *, size_t)
    wchar_t *hid_error(hid_device *)
    char* hid_version_str()
```

## macOS specifics (hidapi 0.15.0 `mac/hid.c`)

**`interface_number` is populated** for USB devices, from `kUSBInterfaceNumber` on the HID device or,
since macOS 13.3, on a parent service; it stays −1 only if both fail:

<!-- verbatim: H mac/hid.c L617-L641 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 617-641:
```c
	/* Interface Number.
	 * We can only retrieve the interface number for USB HID devices.
	 * See below */
	cur_dev->interface_number = -1;

	/* Bus Type */
	transport_prop = IOHIDDeviceGetProperty(dev, CFSTR(kIOHIDTransportKey));

	if (transport_prop != NULL && CFGetTypeID(transport_prop) == CFStringGetTypeID()) {
		if (CFStringCompare((CFStringRef)transport_prop, CFSTR(kIOHIDTransportUSBValue), 0) == kCFCompareEqualTo) {
			int32_t interface_number = -1;
			cur_dev->bus_type = HID_API_BUS_USB;

			/* A IOHIDDeviceRef used to have this simple property,
			 * until macOS 13.3 - we will try to use it. */
			if (try_get_int_property(dev, CFSTR(kUSBInterfaceNumber), &interface_number)) {
				cur_dev->interface_number = interface_number;
			} else {
				/* Otherwise fallback to io_service_t property.
				 * (of one of the parent services). */
				cur_dev->interface_number = read_usb_interface_from_hid_service_parent(hid_service);

				/* If the above doesn't work -
				 * no (known) fallback exists at this point. */
			}
```

**`usage_page` / `usage` are populated**, and a device with several top-level collections is listed
once per collection (same `path`, same `interface_number`):

<!-- verbatim: H mac/hid.c L656-L670 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 656-670:
```c
static struct hid_device_info *create_device_info(IOHIDDeviceRef device)
{
	const int32_t primary_usage_page = get_int_property(device, CFSTR(kIOHIDPrimaryUsagePageKey));
	const int32_t primary_usage = get_int_property(device, CFSTR(kIOHIDPrimaryUsageKey));

	/* Primary should always be first, to match previous behavior. */
	struct hid_device_info *root = create_device_info_with_usage(device, primary_usage_page, primary_usage);
	struct hid_device_info *cur = root;

	if (!root)
		return NULL;

	CFArrayRef usage_pairs = get_usage_pairs(device);

	if (usage_pairs != NULL) {
```

`path` is an IORegistry id string such as `DevSrvsID:4294971234`, not a `/dev` node:

<!-- verbatim: H mac/hid.c L584-L593 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 584-593:
```c
	if (res == KERN_SUCCESS) {
		/* max value of entry_id(uint64_t) is 18446744073709551615 which is 20 characters long,
		   so for (max) "path" string 'DevSrvsID:18446744073709551615' we would need
		   9+1+20+1=31 bytes buffer, but allocate 32 for simple alignment */
		const size_t path_len = 32;
		cur_dev->path = calloc(1, path_len);
		if (cur_dev->path != NULL) {
			snprintf(cur_dev->path, path_len, "DevSrvsID:%llu", entry_id);
		}
	}
```

**Exclusive access by default.** Every `hid_open_path` seizes the device (`kIOHIDOptionsTypeSeizeDevice`):

<!-- verbatim: H mac/hidapi_darwin.h L51-L68 -->
libusb/hidapi 0.15.0 `mac/hidapi_darwin.h`, lines 51-68:
```c
		/** @brief Changes the behavior of all further calls to @ref hid_open or @ref hid_open_path.

			By default on Darwin platform all devices opened by HIDAPI with @ref hid_open or @ref hid_open_path
			are opened in exclusive mode (see kIOHIDOptionsTypeSeizeDevice).

			Since version 0.12.0, @ref HID_API_VERSION >= HID_API_MAKE_VERSION(0, 12, 0)

			@ingroup API
			@param open_exclusive When set to 0 - all further devices will be opened
				in non-exclusive mode. Otherwise - all further devices will be opened
				in exclusive mode.

			@note During the initialisation by @ref hid_init - this property is set to 1 (TRUE).
			This is done to preserve full backward compatibility with previous behavior.

			@note Calling this function before @ref hid_init or after @ref hid_exit has no effect.
		*/
		void HID_API_EXPORT_CALL hid_darwin_set_open_exclusive(int open_exclusive);
```

<!-- verbatim: H mac/hid.c L467-L479 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 467-479:
```c
int HID_API_EXPORT hid_init(void)
{
	register_global_error(NULL);

	if (!hid_mgr) {
		is_macos_10_10_or_greater = (kCFCoreFoundationVersionNumber >= 1151.16); /* kCFCoreFoundationVersionNumber10_10 */
		hid_darwin_set_open_exclusive(1); /* Backward compatibility */
		return init_hid_manager();
	}

	/* Already initialized. */
	return 0;
}
```

<!-- verbatim: H mac/hid.c L1059-L1067 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 1059-1067:
```c
	ret = IOHIDDeviceOpen(dev->device_handle, dev->open_options);
	if (ret != kIOReturnSuccess) {
		register_global_error_format("hid_open_path: failed to open IOHIDDevice from mach entry: (0x%08X) %s", ret, mach_error_string(ret));
		goto return_error;
	}

	/* Create the buffers for receiving data */
	dev->max_input_report_len = (CFIndex) get_max_report_length(dev->device_handle);
	dev->input_report_buf = (uint8_t*) calloc(dev->max_input_report_len, sizeof(uint8_t));
```

The Python binding 0.15.0 does not declare `hid_darwin_set_open_exclusive` (see `chid.pxd` above), so the
bridge always opens the IMU interface exclusively: if another program (XREAL's own app, a second bridge,
Monado) holds it, `open_path` fails, and if the bridge holds it, the others fail. `bridge.py` prints
`device.error()` and this hint.

**Read semantics.** −1 blocks, > 0 waits, 0 polls; a report longer than the buffer is cut to the
buffer's length (the bridge asks for 512 bytes on the Ultra):

<!-- verbatim: H mac/hid.c L1289-L1330 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 1289-1330:
```c
	/* There is no data. Go to sleep and wait for data. */

	if (milliseconds == -1) {
		/* Blocking */
		int res;
		res = cond_wait(dev, &dev->condition, &dev->mutex);
		if (res == 0)
			bytes_read = return_data(dev, data, length);
		else {
			/* There was an error, or a device disconnection. */
			register_error_str(&dev->last_read_error_str, "hid_read_timeout: error waiting for more data");
			bytes_read = -1;
		}
	}
	else if (milliseconds > 0) {
		/* Non-blocking, but called with timeout. */
		int res;
		struct timespec ts;
		struct timeval tv;
		gettimeofday(&tv, NULL);
		TIMEVAL_TO_TIMESPEC(&tv, &ts);
		ts.tv_sec += milliseconds / 1000;
		ts.tv_nsec += (milliseconds % 1000) * 1000000;
		if (ts.tv_nsec >= 1000000000L) {
			ts.tv_sec++;
			ts.tv_nsec -= 1000000000L;
		}

		res = cond_timedwait(dev, &dev->condition, &dev->mutex, &ts);
		if (res == 0) {
			bytes_read = return_data(dev, data, length);
		} else if (res == ETIMEDOUT) {
			bytes_read = 0;
		} else {
			register_error_str(&dev->last_read_error_str, "hid_read_timeout: error waiting for more data");
			bytes_read = -1;
		}
	}
	else {
		/* Purely non-blocking */
		bytes_read = 0;
	}
```

<!-- verbatim: H mac/hid.c L1195-L1208 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 1195-1208:
```c
static int return_data(hid_device *dev, unsigned char *data, size_t length)
{
	/* Copy the data out of the linked list item (rpt) into the
	   return buffer (data), and delete the liked list item. */
	struct input_report *rpt = dev->input_reports;
	size_t len = (length < rpt->len)? length: rpt->len;
	if (data != NULL) {
		memcpy(data, rpt->data, len);
	}
	dev->input_reports = rpt->next;
	free(rpt->data);
	free(rpt);
	return (int) len;
}
```

**Queue limit.** Unread reports queue up to about 30, then the oldest are dropped, so at ~1000 Hz the
reading loop must not stall for more than ~30 ms (the bridge's loop only parses, fuses and sends UDP):

<!-- verbatim: H mac/hid.c L908-L913 -->
libusb/hidapi 0.15.0 `mac/hid.c`, lines 908-913:
```c
		/* Pop one off if we've reached 30 in the queue. This
		   way we don't grow forever if the user never reads
		   anything from the device. */
		if (num_queued > 30) {
			return_data(dev, NULL, 0);
		}
```

Monado's hidapi backend (one day old, M-3090) notes the same timeout rule:

<!-- verbatim: M src/xrt/auxiliary/hid/h_hid_hidapi.c L31-L38 -->
Monado `src/xrt/auxiliary/hid/h_hid_hidapi.c`, lines 31-38:
```c
static int
h_hidapi_read(struct h_hid_device *ohdev, uint8_t *data, size_t length, int milliseconds)
{
	struct hid_hidapi *hadev = (struct hid_hidapi *)ohdev;

	// Any negative timeout blocks, hidapi on macOS only blocks for -1.
	return hid_read_timeout(hadev->handle, data, length, milliseconds < 0 ? -1 : milliseconds);
}
```

## Other notes

- `hid.enumerate(0x3318, 0)` lists every XREAL interface; `bridge.py --list` prints `interface_number`,
  `usage_page`, `usage` and `path` for each, and marks the IMU interface of known models.
- If `import hid` finds a module that has no `hid.device` (a different package with the same module
  name), `bridge.py` says so instead of failing later.
- NOT FOUND: any statement in these sources about macOS asking for Input Monitoring (or any other
  privacy permission) before opening a vendor-defined HID interface. If `open_path` fails with a
  permission error, check System Settings › Privacy & Security.
