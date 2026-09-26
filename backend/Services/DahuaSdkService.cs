using System;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DahuaAttendanceAPI.Services
{
    public sealed class DahuaSdkService : IDisposable
    {
        // Device recognition event structure used internally to forward events
        public class DeviceRecognitionEvent
        {
            public string Source => "DAHUA";
            public DateTime Timestamp { get; set; }
            public string Name { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public int Confidence { get; set; }
            public int ChannelId { get; set; }
            public int EventId { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        // Options DTO for creating an access control user on the device.
        public class CreateAccessControlUserOptions
        {
            // Department numeric id (1..20). Will be sent to device as string in szDepartment.
            public int? Department { get; set; }

            // Period / time section number (e.g., 255 default)
            public int? Period { get; set; }

            // Holiday plan index (e.g., 255 default)
            public int? HolidayPlan { get; set; }

            // Validity range
            public DateTime? ValidFrom { get; set; }
            public DateTime? ValidTo { get; set; }

            // ScheduleMode placeholder; not currently mapped to SDK directly in this phase
            public string? ScheduleMode { get; set; }

            // Permission/authority placeholder; mapping to SDK emAuthority is intentionally omitted
            // because numeric values in the SDK vary by device/firmware. This field is accepted
            // for future use but will not be applied unless explicit numeric mapping is provided.
            public string? Permission { get; set; }

            // UserType placeholder; mapping to SDK emUserType is intentionally omitted for safety.
            public string? UserType { get; set; }

            // TimesUsed placeholder; unlimited/default supported by leaving SDK defaults.
            public string? TimesUsed { get; set; }
        }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_FACEINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szUserID;

        public int nMD5;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5 * 64)]
        public byte[] szMD5;

        public int nEigenMD5;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5 * 64)]
        public byte[] szEigenMD5;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 188)]
        public byte[] byReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_IN_ACCESS_FACE_SERVICE_GET_MANAGED
    {
        public uint dwSize;
        public int nUserNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100 * 32)]
        public byte[] szUserID; // flattened 100 x 32
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100 * 128)]
        public byte[] szUserIDEx; // flattened 100 x 128
        public int bUserIDEx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_OUT_ACCESS_FACE_SERVICE_GET_MANAGED
    {
        public uint dwSize;
        public int nMaxRetNum;
        public IntPtr pFaceInfo;
        public IntPtr pFailCode;
    }

        // ------------------------------------------------------------
        // Read-only: query device for face info for a given access user
        // ------------------------------------------------------------
        public List<DeviceFaceInfo> GetDeviceFacesForUser(string userId)
        {
            var result = new List<DeviceFaceInfo>();
            if (!EnsureLoggedIn()) return result;

            // Use CLIENT_AccessStartFindFaceInfo / DoFind to detect stored face entries
            IntPtr inPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
            try
            {
                // Build NET_IN_ACCESS_FACEINFO_START_FIND
                int inSize = Marshal.SizeOf(typeof(uint)) + 32; // dwSize + szUserID[32]
                inPtr = Marshal.AllocHGlobal(inSize);
                ZeroMemory(inPtr, inSize);
                // dwSize
                Marshal.WriteInt32(inPtr, inSize);
                // copy userId into offset 4 as ANSI bytes, max 31 chars
                var uidBytes = Encoding.Default.GetBytes(userId ?? string.Empty);
                int copyLen = Math.Min(uidBytes.Length, 31);
                Marshal.Copy(uidBytes, 0, IntPtr.Add(inPtr, 4), copyLen);

                // allocate out struct
                int outSize = Marshal.SizeOf(typeof(int)) * 2; // dwSize + nCapNum + nTotalCount simpler
                outPtr = Marshal.AllocHGlobal(outSize);
                ZeroMemory(outPtr, outSize);
                Marshal.WriteInt32(outPtr, outSize);

                long findHandle = CLIENT_AccessStartFindFaceInfo(_loginHandle, inPtr, outPtr, FIND_TIMEOUT);
                if (findHandle == 0) return result;

                try
                {
                    // Prepare DoFind input: start 0, count 10
                    int doInSize = Marshal.SizeOf(typeof(int)) * 2;
                    IntPtr doIn = Marshal.AllocHGlobal(doInSize);
                    ZeroMemory(doIn, doInSize);
                    Marshal.WriteInt32(doIn, doInSize); // dwSize
                    Marshal.WriteInt32(IntPtr.Add(doIn, 4), 0); // nStartNo
                    Marshal.WriteInt32(IntPtr.Add(doIn, 8), 10); // nCount

                    // Prepare out for DoFind: we need pstuInfo pointer and counts
                    int doOutSize = Marshal.SizeOf(typeof(int)) * 2 + IntPtr.Size; // dwSize, nRetNum, pstuInfo
                    IntPtr doOut = Marshal.AllocHGlobal(doOutSize);
                    ZeroMemory(doOut, doOutSize);
                    Marshal.WriteInt32(doOut, doOutSize);

                    bool doOk = CLIENT_AccessDoFindFaceInfo(findHandle, doIn, doOut, FIND_TIMEOUT);
                    if (doOk)
                    {
                        int nRetNum = Marshal.ReadInt32(IntPtr.Add(doOut, 4));
                        IntPtr pstuInfo = Marshal.ReadIntPtr(IntPtr.Add(doOut, 8));
                        if (nRetNum > 0 && pstuInfo != IntPtr.Zero)
                        {
                            int faceInfoSize = Marshal.SizeOf(typeof(NET_FACEINFO));
                            for (int i = 0; i < nRetNum; i++)
                            {
                                IntPtr cur = IntPtr.Add(pstuInfo, i * faceInfoSize);
                                try
                                {
                                    NET_FACEINFO fi = Marshal.PtrToStructure<NET_FACEINFO>(cur);
                                    // Treat each returned entity as a device-enrolled face
                                    // NOTE: The native NET_FACEINFO structure marshalled here does not
                                    // expose an explicit "slot" or "face index" field. The SDK
                                    // provides per-user face entries in a returned list; therefore
                                    // we map the returned order to logical slots deterministically
                                    // using (index + 1). If a future SDK/header exposes a true
                                    // face-slot identifier, replace this mapping with that field.
                                    var df = new DeviceFaceInfo { FaceIndex = i + 1, DeviceEnrolled = true };
                                    result.Add(df);
                                }
                                catch { }
                            }
                        }
                    }

                    FreeHGlobal(ref doIn);
                    FreeHGlobal(ref doOut);
                }
                finally
                {
                    CLIENT_AccessStopFindFaceInfo(findHandle);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] GetDeviceFacesForUser exception: {ex.Message}");
            }
            finally
            {
                FreeHGlobal(ref inPtr);
                FreeHGlobal(ref outPtr);
            }

            // Attempt to fetch actual photo bytes via CLIENT_OperateAccessFaceService GET
            // for each device face we detected. This is optional and best-effort.
            for (int idx = 0; idx < result.Count; idx++)
            {
                try
                {
                    var df = result[idx];
                    // call OperateAccessFaceService GET for this user
                    int inStructSize = Marshal.SizeOf(typeof(int)) * 2 + (100 * 32) + (100 * 128) + 4; // approximate - but we will build minimal
                    // Simpler: prepare NET_IN_ACCESS_FACE_SERVICE_GET via managed struct allocation
                    var inGet = new NET_IN_ACCESS_FACE_SERVICE_GET_MANAGED();
                    inGet.dwSize = (uint)Marshal.SizeOf<NET_IN_ACCESS_FACE_SERVICE_GET_MANAGED>();
                    inGet.nUserNum = 1;
                    var idBytes = Encoding.Default.GetBytes(userId ?? string.Empty);
                    Array.Copy(idBytes, inGet.szUserID, Math.Min(idBytes.Length, inGet.szUserID.Length - 1));

                    IntPtr inGetPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NET_IN_ACCESS_FACE_SERVICE_GET_MANAGED)));
                    Marshal.StructureToPtr(inGet, inGetPtr, false);

                    var outGet = new NET_OUT_ACCESS_FACE_SERVICE_GET_MANAGED();
                    outGet.dwSize = (uint)Marshal.SizeOf<NET_OUT_ACCESS_FACE_SERVICE_GET_MANAGED>();
                    outGet.nMaxRetNum = 1;
                    // allocate space for NET_ACCESS_FACE_INFO
                    IntPtr faceInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NET_ACCESS_FACE_INFO)));
                    ZeroMemory(faceInfoPtr, Marshal.SizeOf(typeof(NET_ACCESS_FACE_INFO)));
                    outGet.pFaceInfo = faceInfoPtr;

                    IntPtr outGetPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NET_OUT_ACCESS_FACE_SERVICE_GET_MANAGED)));
                    Marshal.StructureToPtr(outGet, outGetPtr, false);

                    bool ok = CLIENT_OperateAccessFaceService(_loginHandle, NET_EM_ACCESS_CTL_FACE_SERVICE_GET, inGetPtr, outGetPtr, FIND_TIMEOUT);
                    if (ok)
                    {
                        // marshal pFaceInfo back
                        var returned = Marshal.PtrToStructure<NET_ACCESS_FACE_INFO>(faceInfoPtr);
                        if (returned.nFacePhoto > 0 && returned.pFacePhoto != null && returned.nOutFacePhotoLen != null && returned.nOutFacePhotoLen.Length > 0)
                        {
                            int plen = returned.nOutFacePhotoLen[0];
                            if (plen > 0 && returned.pFacePhoto[0] != IntPtr.Zero)
                            {
                                var bytes = new byte[plen];
                                Marshal.Copy(returned.pFacePhoto[0], bytes, 0, plen);
                                df.PhotoBytes = bytes;
                                df.PhotoLength = plen;
                                // save to wwwroot/enrolled (idempotent, deterministic filename)
                                try
                                {
                                    var enrolledDir = Path.Combine("wwwroot", "enrolled"); Directory.CreateDirectory(enrolledDir);
                                    var safeName = MakeSafeName(userId ?? "user");
                                    var fileName = $"{userId}_{safeName}_device_f{df.FaceIndex}.jpg";
                                    var filePath = Path.Combine(enrolledDir, fileName);
                                    // Only write file if it does not already exist to avoid repeated IO on GET
                                    if (!File.Exists(filePath))
                                    {
                                        // Write atomically: write to temp then move
                                        var tmp = filePath + ".tmp";
                                        File.WriteAllBytes(tmp, bytes);
                                        File.Move(tmp, filePath);
                                    }
                                    df.PhotoFileName = fileName;
                                }
                                catch (Exception ex)
                                {
                                    // If writing fails, do not treat it as fatal; leave PhotoFileName null
                                    Console.WriteLine($"[DahuaSDK] Warning: failed to write device photo for user {userId} face {df.FaceIndex}: {ex.Message}");
                                    df.PhotoFileName = null;
                                }
                            }
                        }
                    }

                    FreeHGlobal(ref inGetPtr);
                    FreeHGlobal(ref outGetPtr);
                    FreeHGlobal(ref faceInfoPtr);
                }
                catch (Exception) { }
            }

            return result;
        }

    // Simple device face DTO returned by SDK wrapper
    public sealed class DeviceFaceInfo
    {
        public int FaceIndex { get; set; }
        public bool DeviceEnrolled { get; set; }
        public byte[]? PhotoBytes { get; set; }
        public string? PhotoFileName { get; set; }
        public int PhotoLength { get; set; }
    }


    // --- Begin Dahua event structs (header-accurate, x64-aware) ---


    // We'll use header-accurate marshaling but keep nested helper types prefixed to avoid collision
    [StructLayout(LayoutKind.Sequential)]
    public struct Dhx_DH_RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Dhx_DH_POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Dhx_DH_MSG_OBJECT
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 62)]
        public string szObjectSubType;
        public ushort wColorLogoIndex;
        public ushort wSubBrand;
        public byte byReserved1;
        [MarshalAs(UnmanagedType.I1)]
        public bool bPicEnble;
        public IntPtr stPicInfo_ptr; // DH_PIC_INFO (not parsed here)
        [MarshalAs(UnmanagedType.I1)]
        public bool bShotFrame;
        [MarshalAs(UnmanagedType.I1)]
        public bool bColor;
        public byte byReserved2;
        public byte byTimeType;
        public NET_TIME_EX stuCurrentTime;
        public NET_TIME_EX stuStartTime;
        public NET_TIME_EX stuEndTime;
        public Dhx_DH_RECT stuOriginalBoundingBox;
        public Dhx_DH_RECT stuSignBoundingBox;
        public uint dwCurrentSequence;
        public uint dwBeginSequence;
        public uint dwEndSequence;
        public long nBeginFileOffset;
        public long nEndFileOffset;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Dhx_FACERECOGNITION_PERSON_INFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string szPersonName;
        public ushort wYear;
        public byte byMonth;
        public byte byDay;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szID;
        public byte bImportantRank;
        public byte bySex;
        public ushort wFacePicNum;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Dhx_CANDIDATE_INFO
    {
        public Dhx_FACERECOGNITION_PERSON_INFO stPersonInfo;
        public byte bySimilarity;
        public byte byRange;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byReserved1;
        public NET_TIME stTime;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szAddress;
        [MarshalAs(UnmanagedType.I1)]
        public bool bIsHit;
        public int nChannelID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szChannelString;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Dhx_DEV_EVENT_FACERECOGNITION_INFO
    {
        public int nChannelID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szName;
        public int nEventID;
        public NET_TIME_EX UTC;
        public Dhx_DH_MSG_OBJECT stuObject;
        public int nCandidateNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 50)]
        public Dhx_CANDIDATE_INFO[] stuCandidates;
        public byte bEventAction;
        public byte byImageIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byReserved1;
        [MarshalAs(UnmanagedType.I1)]
        public bool bGlobalScenePic;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szUID;
    }
    // --- End Dahua event structs ---

    // =================================================================
    // DH_ALARM_FACEINFO_COLLECT struct
    //
    // Header: dhnetsdk.h — tagALARM_FACEINFO_COLLECT_INFO
    // lCommand: DH_ALARM_FACEINFO_COLLECT = 0x3240
    //
    // Layout:
    //   int          nAction        (4 bytes)   — 1=start, 2=stop
    //   NET_TIME_EX  stuTime        (36 bytes)
    //   double       dbPTS          (8 bytes)
    //   char[32]     szUserID       (32 bytes)
    //   byte[512]    byReserved     (512 bytes)
    //
    // Total: 4 + 36 + 8 + 32 + 512 = 592 bytes (natural alignment, no Pack=1).
    // =================================================================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ALARM_FACEINFO_COLLECT_INFO
    {
        /// <summary>1 = collection started, 2 = collection stopped/completed.</summary>
        public int nAction;

        public NET_TIME_EX stuTime;

        public double dbPTS;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] szUserID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] byReserved;
    }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_IN_GET_ACCESS_PERSON_COLLECTION_CAPS
        {
            public uint dwSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_OUT_GET_ACCESS_PERSON_COLLECTION_CAPS
        {
            public uint dwSize;
            public int bSupportMixedCollection;
            public uint nSupportCollectionType;
            public int nCardReaderIDListCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32 * 32)]
            public byte[] szCardReaderIDList;

            public int nFingerReaderIDListCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32 * 32)]
            public byte[] szFingerReaderIDList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_NOTIFY_PERSON_COLLECTION_DATA_INFO
        {
            public uint nLength;
            public uint nOffset;
            public uint nType;
            public uint nErrorCode;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)]
            public byte[] szResvered;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_NOTIFY_PERSON_COLLECTION_INFO
        {
            public NET_TIME stuUTC;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] szUUID;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] szUserID;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public NET_NOTIFY_PERSON_COLLECTION_DATA_INFO[] stuDataInfo;

            public int nDataInfoNum;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1020)]
            public byte[] szResvered;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_IN_ATTACH_ACCESS_PERSON_COLLECTION
        {
            public uint dwSize;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] szResvered;

            public IntPtr cbNotify;
            public long dwUser;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_OUT_ATTACH_ACCESS_PERSON_COLLECTION
        {
            public uint dwSize;
        }

        public sealed class AccessPersonCollectionCapabilities
        {
            public bool Success { get; init; }
            public uint SdkError { get; init; }
            public bool SupportsMixedCollection { get; init; }
            public uint SupportedCollectionTypeMask { get; init; }
            public bool SupportsFaceCollection =>
                (SupportedCollectionTypeMask & 1u) != 0;
            public int CardReaderCount { get; init; }
            public string[] CardReaderIds { get; init; } = Array.Empty<string>();
            public int FingerReaderCount { get; init; }
            public string[] FingerReaderIds { get; init; } = Array.Empty<string>();
            public long NotificationHandle { get; init; }
        }

        private void RegisterNativeCallbacks()
        {
            try
            {
                // Assign delegates and keep references
                _nativeMessCallback = NativeMessCallback;
                _nativeMessCallbackEx1 = NativeMessCallbackEx1;
                _nativeSnapCallback = NativeSnapCallback;

                // Register with native SDK
                CLIENT_SetDVRMessCallBack(_nativeMessCallback, IntPtr.Zero);
                CLIENT_SetDVRMessCallBackEx1(_nativeMessCallbackEx1, IntPtr.Zero);
                CLIENT_SetSnapRevCallBack(_nativeSnapCallback, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to register native callbacks: " + ex);
            }
        }

        private bool AttachRecognitionEvents()
        {
            if (_loginHandle == 0)
            {
                Console.WriteLine("[DahuaSDK] Recognition subscription skipped: device is not logged in.");
                return false;
            }

            if (_analyzerHandle != 0)
            {
                Console.WriteLine($"[DahuaSDK] Recognition subscription already active: handle={_analyzerHandle}");
                return true;
            }

            _analyzerDataCallback ??= NativeAnalyzerDataCallback;

                int channel = _recognitionChannel;
                // Use EVENT_IVS_ALL (0x00000001) per SDK sample instead of 0xFFFFFFFF
                const uint eventMask = 0x00000001; // EVENT_IVS_ALL

            try
            {
                long handle = CLIENT_RealLoadPictureEx(
                    _loginHandle,
                    channel,
                    eventMask,
                    true,
                    _analyzerDataCallback,
                    0,
                    IntPtr.Zero);

                uint sdkError = SafeGetLastError();
                Console.WriteLine(
                    "[DahuaSDK] Recognition subscription: "
                    + $"loginHandle={_loginHandle} channel={channel} "
                    + $"eventMask=0x{eventMask:X8} EVENT_IVS_FACERECOGNITION=0x00000117 "
                    + $"handle={handle} sdkError={sdkError} hexError=0x{sdkError:X8}");

                _analyzerHandle = handle > 0 ? handle : 0;
                return _analyzerHandle != 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Recognition subscription exception: {ex.Message}");
                _analyzerHandle = 0;
                return false;
            }
        }

        private void DetachRecognitionEvents()
        {
            if (_analyzerHandle == 0)
                return;

            try
            {
                bool result = CLIENT_StopLoadPic(_analyzerHandle);
                uint sdkError = SafeGetLastError();
                Console.WriteLine(
                    $"[DahuaSDK] Recognition subscription stopped: handle={_analyzerHandle} result={result} sdkError={sdkError} hexError=0x{sdkError:X8}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Recognition subscription stop exception: {ex.Message}");
            }
            finally
            {
                _analyzerHandle = 0;
            }
        }

        private int NativeAnalyzerDataCallback(
            long analyzerHandle,
            uint alarmType,
            IntPtr alarmInfo,
            IntPtr buffer,
            uint bufferSize,
            long user,
            int sequence,
            IntPtr reserved)
        {
            // Diagnostic: prove the native analyzer callback is invoked at all
            try
            {
                Console.WriteLine($"[CALLBACK TEST] NativeAnalyzerDataCallback INVOKED: handle={analyzerHandle} alarmType=0x{alarmType:X8} bufferSize={bufferSize} user={user} sequence={sequence}");
            }
            catch { }

            const uint eventFaceRecognition = 0x00000117;

            Console.WriteLine(
                "[DahuaSDK] Analyzer callback received: "
                + $"handle={analyzerHandle} alarmType=0x{alarmType:X8} "
                + $"alarmInfo={(alarmInfo == IntPtr.Zero ? "null" : "non-null")} "
                + $"bufferSize={bufferSize} sequence={sequence}");

            if (alarmType != eventFaceRecognition)
                return 0;

            Console.WriteLine("[DahuaSDK] EVENT_IVS_FACERECOGNITION received through CLIENT_RealLoadPictureEx.");

            if (alarmInfo == IntPtr.Zero)
            {
                Console.WriteLine("[DahuaSDK] Face-recognition callback contained a null alarm-info pointer.");
                return 0;
            }

            try
            {
                var managed = Marshal.PtrToStructure<Dhx_DEV_EVENT_FACERECOGNITION_INFO>(alarmInfo);
                string name = managed.szName ?? string.Empty;
                string userId = string.Empty;
                int confidence = 0;
                var rect = managed.stuObject.stuOriginalBoundingBox;

                if (managed.nCandidateNum > 0 && managed.stuCandidates != null && managed.stuCandidates.Length > 0)
                {
                    var candidate = managed.stuCandidates[0];
                    userId = candidate.stPersonInfo.szID ?? string.Empty;
                    confidence = candidate.bySimilarity;
                }

                Console.WriteLine(
                    $"[DahuaSDK] Face recognition parsed: channel={managed.nChannelID} "
                    + $"eventId={managed.nEventID} candidates={managed.nCandidateNum} "
                    + $"userId='{userId}' confidence={confidence}");

                OnDeviceRecognition?.Invoke(new DeviceRecognitionEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Name = string.IsNullOrWhiteSpace(name) ? userId : name,
                    UserId = userId,
                    Confidence = confidence,
                    ChannelId = managed.nChannelID,
                    EventId = managed.nEventID,
                    X = rect.left,
                    Y = rect.top,
                    Width = rect.right - rect.left,
                    Height = rect.bottom - rect.top
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Failed to parse analyzer face-recognition event: {ex}");
            }

            return 0;
        }

        // Native callback implementations - parse minimal fields and raise managed event
        private bool NativeMessCallback(int lCommand, long lLoginID, IntPtr pBuf, uint dwBufLen, IntPtr pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            // Diagnostic: prove the general native message callback is invoked
            try
            {
                Console.WriteLine($"[CALLBACK TEST] NativeMessCallback INVOKED: lCommand=0x{lCommand:X8} loginId={lLoginID} bufLen={dwBufLen} port={nDVRPort}");
            }
            catch { }

            const int EVENT_IVS_FACERECOGNITION  = 0x00000117;
            const int DH_ALARM_FACEINFO_COLLECT  = 0x00003240;

            try
            {
                if (lCommand == EVENT_IVS_FACERECOGNITION)
                {
                    if (pBuf == IntPtr.Zero || dwBufLen == 0)
                        return true;

                    try
                    {
                        var managed = Marshal.PtrToStructure<Dhx_DEV_EVENT_FACERECOGNITION_INFO>(pBuf);
                        string name = managed.szName ?? string.Empty;
                        string userId = string.Empty;
                        int confidence = 0;
                        int eventId = managed.nEventID;
                        var rect = managed.stuObject.stuOriginalBoundingBox;

                        if (managed.nCandidateNum > 0 && managed.stuCandidates != null && managed.stuCandidates.Length > 0)
                        {
                            var cand = managed.stuCandidates[0];
                            userId = cand.stPersonInfo.szID ?? string.Empty;
                            confidence = cand.bySimilarity;
                        }

                        var ev = new DeviceRecognitionEvent
                        {
                            Timestamp = DateTime.UtcNow,
                            Name = string.IsNullOrWhiteSpace(name) ? (userId ?? string.Empty) : name,
                            UserId = userId ?? string.Empty,
                            Confidence = confidence,
                            ChannelId = managed.nChannelID,
                            EventId = eventId,
                            X = rect.left,
                            Y = rect.top,
                            Width = rect.right - rect.left,
                            Height = rect.bottom - rect.top
                        };

                        OnDeviceRecognition?.Invoke(ev);
                    }
                    catch (Exception mex)
                    {
                        Console.WriteLine("Failed to marshal DEV_EVENT_FACERECOGNITION_INFO (Dhx): " + mex);
                    }
                }
                else if (lCommand == DH_ALARM_FACEINFO_COLLECT)
                {
                    // -------------------------------------------------------
                    // DH_ALARM_FACEINFO_COLLECT (0x3240)
                    //
                    // Fires when the device's self-service face collection
                    // mode (EM_SYS_MODE_FACECOLLECT) starts or completes.
                    //
                    // ALARM_FACEINFO_COLLECT_INFO fields:
                    //   nAction  : 1 = start, 2 = stop (collection finished)
                    //   szUserID : UserId reported by the device — populated
                    //              from whatever identity the person entered on
                    //              the device terminal. May be empty.
                    //
                    // IMPORTANT: This event fires when the device's collection
                    // workflow runs. szUserID comes from the device/terminal,
                    // not from a server-side per-user trigger (no such SDK
                    // command exists). FaceEnrollmentCallbackService validates
                    // the UserId exists in the DB before marking FaceEnrolled.
                    // -------------------------------------------------------

                    if (pBuf == IntPtr.Zero || dwBufLen == 0)
                        return true;

                    try
                    {
                        var managed = Marshal.PtrToStructure<ALARM_FACEINFO_COLLECT_INFO>(pBuf);
                        string userId = managed.szUserID != null
                            ? ReadAnsiString(managed.szUserID)
                            : string.Empty;

                        Console.WriteLine(
                            $"[DahuaSDK] DH_ALARM_FACEINFO_COLLECT: action={managed.nAction} userId='{userId}'");

                        var ev = new DeviceFaceCollectedEvent
                        {
                            Timestamp = DateTime.UtcNow,
                            UserId    = userId.Trim(),
                            Action    = managed.nAction   // 1=start, 2=stop
                        };

                        OnDeviceFaceCollected?.Invoke(ev);
                    }
                    catch (Exception mex)
                    {
                        Console.WriteLine("Failed to marshal ALARM_FACEINFO_COLLECT_INFO: " + mex);
                    }
                }
                // All other lCommand values are silently ignored.
            }
            catch (Exception ex)
            {
                Console.WriteLine("NativeMessCallback error: " + ex);
            }

            return true;
        }

        private bool NativeMessCallbackEx1(int lCommand, long lLoginID, IntPtr pBuf, uint dwBufLen, IntPtr pchDVRIP, int nDVRPort, bool bAlarmAckFlag, int nEventID, IntPtr dwUser)
        {
            // Reuse simpler handler
            return NativeMessCallback(lCommand, lLoginID, pBuf, dwBufLen, pchDVRIP, nDVRPort, dwUser);
        }

        private void NativeSnapCallback(long lLoginID, IntPtr pBuf, uint RevLen, uint EncodeType, uint CmdSerial, IntPtr dwUser)
        {
            // Snap callback may deliver image bytes; we don't process here but could forward metadata
        }

        // Heuristic extraction of ascii printable string from a byte blob
        private static string ExtractAsciiString(byte[] data)
        {
            try
            {
                int start = -1;
                int bestStart = -1;
                int bestLen = 0;
                int curLen = 0;

                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];
                    if (b >= 32 && b <= 126) // printable ASCII
                    {
                        if (start == -1) start = i;
                        curLen++;
                    }
                    else
                    {
                        if (curLen > bestLen)
                        {
                            bestLen = curLen;
                            bestStart = start;
                        }
                        start = -1;
                        curLen = 0;
                    }
                }

                if (curLen > bestLen)
                {
                    bestLen = curLen;
                    bestStart = start;
                }

                if (bestLen >= 3 && bestStart >= 0)
                {
                    return Encoding.ASCII.GetString(data, bestStart, bestLen).Trim('\0', ' ');
                }
            }
            catch { }

            return null;
        }

        // Event raised when an SDK recognition/alarm message is received and parsed
        public event Action<DeviceRecognitionEvent>? OnDeviceRecognition;

        // ============================================================
        // DH_ALARM_FACEINFO_COLLECT event
        //
        // Raised when the device fires DH_ALARM_FACEINFO_COLLECT (0x3240).
        // nAction == 1 → collection started on device
        // nAction == 2 → collection stopped / completed on device
        //
        // NOTE: szUserID is whatever the device/terminal reports. It is
        // NOT set by a server-side per-user command — no such SDK function
        // exists. Consumers MUST validate UserId against the database
        // before trusting it for any enrollment status update.
        // ============================================================
        public event Action<DeviceFaceCollectedEvent>? OnDeviceFaceCollected;

        // DTO for DH_ALARM_FACEINFO_COLLECT events
        public sealed class DeviceFaceCollectedEvent
        {
            /// <summary>1 = collection started, 2 = collection stopped/completed.</summary>
            public int Action { get; set; }

            /// <summary>
            /// UserId reported by the device terminal.
            /// May be empty — validate before use.
            /// </summary>
            public string UserId { get; set; } = string.Empty;

            public DateTime Timestamp { get; set; }
        }

        // Internal delegate types for native callbacks
        private delegate bool MessCallback(int lCommand, long lLoginID, IntPtr pBuf, uint dwBufLen, IntPtr pchDVRIP, int nDVRPort, IntPtr dwUser);
        private delegate bool MessCallbackEx1(int lCommand, long lLoginID, IntPtr pBuf, uint dwBufLen, IntPtr pchDVRIP, int nDVRPort, bool bAlarmAckFlag, int nEventID, IntPtr dwUser);
        private delegate void SnapRevCallback(long lLoginID, IntPtr pBuf, uint RevLen, uint EncodeType, uint CmdSerial, IntPtr dwUser);
        private delegate void PersonCollectionCallback(long lAttachHandle, IntPtr pstuPersonCollection, IntPtr pBinData, uint dwBinDataLen, long dwUser);
        private delegate int AnalyzerDataCallback(long lAnalyzerHandle, uint dwAlarmType, IntPtr pAlarmInfo, IntPtr pBuffer, uint dwBufSize, long dwUser, int nSequence, IntPtr reserved);

        // Keep references alive to prevent GC
        private MessCallback? _nativeMessCallback;
        private MessCallbackEx1? _nativeMessCallbackEx1;
        private SnapRevCallback? _nativeSnapCallback;
        private PersonCollectionCallback? _personCollectionCallback;
        private long _personCollectionAttachHandle;
        private AnalyzerDataCallback? _analyzerDataCallback;
        private long _analyzerHandle;
        // recognition subscription channel (default 0)
        private int _recognitionChannel = 0;
        // ============================================================
        // SDK
        // ============================================================

        private const string SDK_DLL = @"SDK\dhnetsdk.dll";

        private const int SDK_TIMEOUT = 10000;
        private const int FIND_TIMEOUT = 5000;

        // Dahua header:
        // NET_ACCESS_FACE_INFO::pFacePhoto[5]
        // each photo max 120 KB.
        private const int MAX_FACE_PHOTO_SIZE = 120 * 1024;

        private const int MAX_USER_ID_BYTES = 32;
        private const int MAX_USER_NAME_BYTES = 32;

        private const int MAX_ACCESS_USERS_PER_QUERY = 100;
        private const int MAX_LEGACY_USERS_PER_QUERY = 100;

        // ============================================================
        // Dahua native enum values
        // ============================================================

        private const int NET_EM_ACCESS_CTL_USER_SERVICE_INSERT = 0;
        private const int NET_EM_ACCESS_CTL_USER_SERVICE_GET = 1;
        private const int NET_EM_ACCESS_CTL_USER_SERVICE_REMOVE = 2;
        private const int NET_EM_ACCESS_CTL_FACE_SERVICE_INSERT = 0;
        private const int NET_EM_ACCESS_CTL_FACE_SERVICE_GET = 1;

        // ============================================================
        // Native SDK functions
        // ============================================================

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_Init")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_Init(
            IntPtr cbDisConnect,
            IntPtr dwUser);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_Cleanup")]
        private static extern void CLIENT_Cleanup();

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_GetLastError")]
        private static extern uint CLIENT_GetLastError();

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_LoginWithHighLevelSecurity")]
        private static extern long CLIENT_LoginWithHighLevelSecurity(
            ref NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY pstInParam,
            ref NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY pstOutParam);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_Logout")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_Logout(
            long lLoginID);

        // ============================================================
        // Legacy Attendance API
        // ============================================================

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_Attendance_FindUser")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_Attendance_FindUser(
            long lLoginID,
            ref NET_IN_ATTENDANCE_FINDUSER pstuInFindUser,
            ref NET_OUT_ATTENDANCE_FINDUSER pstuOutFindUser,
            int nWaitTime);

        // ============================================================
        // Modern Access User API
        // ============================================================

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StartFindUserInfo")]
        private static extern long CLIENT_StartFindUserInfo(
            long lLoginID,
            ref NET_IN_USERINFO_START_FIND pstIn,
            ref NET_OUT_USERINFO_START_FIND pstOut,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_DoFindUserInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_DoFindUserInfo(
            long lFindHandle,
            ref NET_IN_USERINFO_DO_FIND pstIn,
            ref NET_OUT_USERINFO_DO_FIND pstOut,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StopFindUserInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StopFindUserInfo(
            long lFindHandle);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_OperateAccessUserService")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_OperateAccessUserService(
            long lLoginID,
            int emType,
            IntPtr pstInParam,
            IntPtr pstOutParam,
            int nWaitTime);

        // ============================================================
        // Face API
        // ============================================================

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_OperateAccessFaceService")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_OperateAccessFaceService(
            long lLoginID,
            int emType,
            IntPtr pstInParam,
            IntPtr pstOutParam,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_CaptureAccessPersonCollectionCmd")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_CaptureAccessPersonCollectionCmd(
            long lLoginID,
            IntPtr pstInParam,
            IntPtr pstOutParam,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_GetAccessPersonCollectionCaps")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_GetAccessPersonCollectionCaps(
            long lLoginID,
            IntPtr pstuInParam,
            IntPtr pstuOutParam,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_AttachAccessPersonCollection")]
        private static extern long CLIENT_AttachAccessPersonCollection(
            long lLoginID,
            IntPtr pstuInParam,
            IntPtr pstuOutParam,
            int nWaitTime);

        [DllImport(SDK_DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "CLIENT_AccessStartFindFaceInfo")]
        private static extern long CLIENT_AccessStartFindFaceInfo(long lLoginID, IntPtr pstIn, IntPtr pstOut, int nWaitTime);

        [DllImport(SDK_DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "CLIENT_AccessDoFindFaceInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_AccessDoFindFaceInfo(long lFindHandle, IntPtr pstIn, IntPtr pstOut, int nWaitTime);

        [DllImport(SDK_DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "CLIENT_AccessStopFindFaceInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_AccessStopFindFaceInfo(long lFindHandle);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_DetachAccessPersonCollection")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_DetachAccessPersonCollection(
            long lAttachHandle);

        // Event / callback registration from native SDK
        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_SetDVRMessCallBack")]
        private static extern void CLIENT_SetDVRMessCallBack(
            MessCallback cbMessage,
            IntPtr user);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_SetDVRMessCallBackEx1")]
        private static extern void CLIENT_SetDVRMessCallBackEx1(
            MessCallbackEx1 cbMessage,
            IntPtr dwUser);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_SetSnapRevCallBack")]
        private static extern void CLIENT_SetSnapRevCallBack(
            SnapRevCallback cb,
            IntPtr dwUser);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_RealLoadPictureEx")]
        private static extern long CLIENT_RealLoadPictureEx(
            long lLoginID,
            int nChannelID,
            uint dwAlarmType,
            [MarshalAs(UnmanagedType.Bool)] bool bNeedPicFile,
            AnalyzerDataCallback cbAnalyzerData,
            long dwUser,
            IntPtr reserved);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StopLoadPic")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StopLoadPic(
            long lAnalyzerHandle);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StartListen")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StartListen(
            long lLoginID);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StartListenEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StartListenEx(
            long lLoginID);

        // Face recognition history query APIs (diagnostic)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct NET_PIC_INFO
        {
            public uint dwFileLenth;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szFilePath;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] bReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct NET_IN_STARTMULTIFIND_FACERECONGNITIONRECORD
        {
            public uint dwSize;
            public NET_TIME stStartTime;
            public NET_TIME stEndTime;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szMachineAddress;

            public int nAlarmType;
            public int abPersonInfo; // BOOL
            public Dhx_FACERECOGNITION_PERSON_INFO stPersonInfo;
            public IntPtr pChannelID;
            public int nChannelCount;
            public int nGroupIdNum;

            // Reserve space for group ids: MAX_GOURP_NUM x 64
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128 * 64)]
            public byte[] szGroupId;

            public int abPersonExInfo;
            public Dhx_FACERECOGNITION_PERSON_INFO stPersonInfoEx; // reuse existing smaller struct
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_OUT_STARTMULTIFIND_FACERECONGNITIONRECORD
        {
            public uint dwSize;
            public int nTotalCount;
            public long lFindHandle;
            public int nToken;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_IN_DOFIND_FACERECONGNITIONRECORD
        {
            public uint dwSize;
            public int nTotalCount;
            public long lFindHandle;
            public int nBeginNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DOFIND_FACERECONGNITIONRECORD_INFO
        {
            // BOOL as int
            public int bGlobalScenePic;
            public NET_PIC_INFO stGlobalScenePic;
            public Dhx_DH_MSG_OBJECT stuObject;
            public NET_PIC_INFO stObjectPic;
            public int nCandidateNum;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 50)]
            public Dhx_CANDIDATE_INFO[] stuCandidates;

            // skipping candidate pic paths marshaling detail - reserved
            public NET_TIME stTime;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szAddress;

            public int nChannelId;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] bReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_OUT_DOFIND_FACERECONGNITIONRECORD
        {
            public uint dwSize;
            public IntPtr stuResults; // pointer to NET_DOFIND_FACERECONGNITIONRECORD_INFO
            public int nResultNum;
            public int nTotalCount;
        }

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StartMultiFindFaceRecognitionRecord")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StartMultiFindFaceRecognitionRecord(
            long lLoginID,
            ref NET_IN_STARTMULTIFIND_FACERECONGNITIONRECORD pstInParam,
            ref NET_OUT_STARTMULTIFIND_FACERECONGNITIONRECORD pstOutParam,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_DoFindFaceRecognitionRecord")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_DoFindFaceRecognitionRecord(
            ref NET_IN_DOFIND_FACERECONGNITIONRECORD pstInParam,
            ref NET_OUT_DOFIND_FACERECONGNITIONRECORD pstOutParam,
            int nWaitTime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StopFindFaceRecognitionRecord")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StopFindFaceRecognitionRecord(
            long lFindHandle);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_StopListen")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_StopListen(
            long lLoginID);

        // ============================================================
        // Device config API
        //
        // Used to read / write device-wide configuration, including
        // NET_CFG_COLLECT_USER_INFO_CFG (collection mode enable/disable).
        //
        // dwCommand: use the NET_EM_CFG_* constants from the SDK header.
        // lChannel : -1 for device-level config (no channel).
        // ============================================================

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_GetDevConfig")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_GetDevConfig(
            long lLoginID,
            uint dwCommand,
            int lChannel,
            IntPtr lpOutBuffer,
            uint dwOutBufferSize,
            ref uint lpBytesReturned,
            int waittime);

        [DllImport(
            SDK_DLL,
            CallingConvention = CallingConvention.StdCall,
            EntryPoint = "CLIENT_SetDevConfig")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CLIENT_SetDevConfig(
            long lLoginID,
            uint dwCommand,
            int lChannel,
            IntPtr lpInBuffer,
            uint dwInBufferSize,
            int waittime);

        // ============================================================
        // State
        // ============================================================

        private long _loginHandle;
        // Last successful device login credentials. Used to silently reconnect when a
        // device-facing operation finds no live session (e.g. after process restart or
        // an SDK session drop). Persisted locally so the reconnect survives restarts.
        private string _lastLoginIp = string.Empty;
        private int _lastLoginPort = 37777;
        private string _lastLoginUsername = string.Empty;
        private string _lastLoginPassword = string.Empty;
        private readonly object _sessionLock = new object();
        private bool _sessionStateLoaded;
        // whether CLIENT_StartListen succeeded and needs stopping on logout
        private bool _startListenActive;
        // whether CLIENT_StartListenEx succeeded and needs stopping on logout
        private bool _startListenExActive;
        private bool _initialized;
        private bool _disposed;
        private bool _interopDiagnosticsDumped;

        public long LoginHandle => _loginHandle;

        public bool IsInitialized => _initialized;

        public bool IsLoggedIn => _loginHandle != 0;

        // ============================================================
        // Session reconnect
        // ============================================================

        /// <summary>
        /// Ensures an active SDK login exists. If not, silently reconnects using the
        /// last successful device login credentials (in-memory or persisted). Returns
        /// true only when a login is active afterwards.
        /// </summary>
        public bool EnsureLoggedIn()
        {
            if (IsLoggedIn)
                return true;

            lock (_sessionLock)
            {
                if (IsLoggedIn)
                    return true;

                LoadSessionStateIfNeeded();

                // Diagnostic: report whether we have persisted session state
                try
                {
                    Console.WriteLine($"[DahuaSDK DIAG] EnsureLoggedIn: _lastLoginIp='{_lastLoginIp}' _lastLoginPort={_lastLoginPort} _lastLoginUsername='{_lastLoginUsername}'");
                }
                catch { }

                if (string.IsNullOrWhiteSpace(_lastLoginIp) ||
                    string.IsNullOrWhiteSpace(_lastLoginUsername))
                {
                    Console.WriteLine("[DahuaSDK DIAG] EnsureLoggedIn: No saved session state (no IP/username) - won\'t attempt auto-login.");
                    return false;
                }

                try
                {
                    Console.WriteLine($"[DahuaSDK DIAG] EnsureLoggedIn: Attempting auto-login to {_lastLoginIp}:{_lastLoginPort} as {_lastLoginUsername} (password hidden)");
                    bool loginResult = Login(
                        _lastLoginIp,
                        _lastLoginPort,
                        _lastLoginUsername,
                        _lastLoginPassword);

                    // If login failed, capture SDK last error for diagnostics
                    if (!loginResult)
                    {
                        try
                        {
                            uint lastErr = SafeGetLastError();
                            Console.WriteLine($"[DahuaSDK DIAG] EnsureLoggedIn: auto-login failed. LoginResult={loginResult} SafeGetLastError={lastErr} (0x{lastErr:X8}) IsLoggedIn={IsLoggedIn}");
                        }
                        catch { Console.WriteLine("[DahuaSDK DIAG] EnsureLoggedIn: auto-login failed and SafeGetLastError failed to execute."); }
                    }

                    return loginResult;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DahuaSDK] Auto-reconnect login failed: {ex.Message}");
                    return false;
                }
            }
        }

        internal string SessionFilePath =>
            Path.Combine(AppContext.BaseDirectory, "dahua_device_session.json");

        private DeviceSessionState LoadSessionStateIfNeeded()
        {
            if (_sessionStateLoaded)
                return null;

            _sessionStateLoaded = true;

            try
            {
                if (!File.Exists(SessionFilePath))
                    return null;

                var state = JsonSerializer.Deserialize<DeviceSessionState>(
                    File.ReadAllText(SessionFilePath));

                if (state != null && !string.IsNullOrWhiteSpace(state.Ip))
                {
                    _lastLoginIp = state.Ip;
                    _lastLoginPort = state.Port;
                    _lastLoginUsername = state.Username;
                    _lastLoginPassword = state.Password;
                    return state;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Unable to load device session state: {ex.Message}");
            }

            return null;
        }

        private void SaveSessionState()
        {
            try
            {
                var state = new DeviceSessionState
                {
                    Ip = _lastLoginIp ?? string.Empty,
                    Port = _lastLoginPort,
                    Username = _lastLoginUsername ?? string.Empty,
                    Password = _lastLoginPassword ?? string.Empty
                };

                File.WriteAllText(
                    SessionFilePath,
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Unable to save device session state: {ex.Message}");
            }
        }

        private sealed class DeviceSessionState
        {
            public string Ip { get; set; } = string.Empty;
            public int Port { get; set; } = 37777;
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }

        // ============================================================
        // Initialize
        // ============================================================

        public bool Initialize()
        {
            ThrowIfDisposed();

            if (_initialized)
                return true;

            if (!Environment.Is64BitProcess)
            {
                Console.WriteLine(
                    "Dahua SDK requires an x64 process. " +
                    "The supplied dhnetsdk.dll is x64.");

                return false;
            }

            try
            {
                bool result = CLIENT_Init(
                    IntPtr.Zero,
                    IntPtr.Zero);

                uint error = SafeGetLastError();

                Console.WriteLine("--------------------------------");
                Console.WriteLine("DAHUA SDK INITIALIZATION");
                Console.WriteLine($"Result       : {result}");
                Console.WriteLine($"SDK Error    : {error}");
                Console.WriteLine($"Process      : x{(Environment.Is64BitProcess ? 64 : 86)}");
                Console.WriteLine($"Pointer Size : {IntPtr.Size}");
                Console.WriteLine("--------------------------------");
                    // Dump interop diagnostics once for troubleshooting ABI/layout issues.
                    try
                    {
                        if (!_interopDiagnosticsDumped)
                        {
                            DumpInteropDiagnostics();
                            _interopDiagnosticsDumped = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Interop diagnostics failed: " + ex);
                    }

                if (!result)
                {
                    _initialized = false;
                    return false;
                }

                _initialized = true;

                // Register native callbacks to receive device messages/snaps
                RegisterNativeCallbacks();

                return true;
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine(
                    $"Dahua SDK DLL not found: {SDK_DLL}");

                Console.WriteLine(ex.Message);

                return false;
            }
            catch (BadImageFormatException ex)
            {
                Console.WriteLine(
                    "Dahua SDK architecture mismatch. " +
                    "The supplied SDK is x64. " +
                    "Run the application as x64.");

                Console.WriteLine(ex.Message);

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"SDK initialization exception: {ex}");

                return false;
            }
        }

        // ============================================================
        // Login
        // ============================================================

        // Diagnostics helper methods
        private void DumpInteropDiagnostics()
        {
            Console.WriteLine("--- Interop Diagnostics ---");
            Console.WriteLine($"IntPtr.Size = {IntPtr.Size}");
            Console.WriteLine($"Environment.Is64BitProcess = {Environment.Is64BitProcess}");

            try
            {
                Console.WriteLine($"Size NET_ACCESS_USER_INFO = {Marshal.SizeOf<NET_ACCESS_USER_INFO>()}");
                Console.WriteLine($"Size NET_IN_ACCESS_USER_SERVICE_INSERT = {Marshal.SizeOf<NET_IN_ACCESS_USER_SERVICE_INSERT>()}");
                Console.WriteLine($"Size NET_OUT_ACCESS_USER_SERVICE_INSERT = {Marshal.SizeOf<NET_OUT_ACCESS_USER_SERVICE_INSERT>()}");
                Console.WriteLine($"Size NET_ACCESS_FACE_INFO = {Marshal.SizeOf<NET_ACCESS_FACE_INFO>()}");

                // Print some field offsets from NET_ACCESS_USER_INFO
                Console.WriteLine("Offsets for NET_ACCESS_USER_INFO:");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "szUserID");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "szName");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "emUserType");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "nUserStatus");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "nUserTime");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "szPsw");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "nDoors");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "nTimeSectionNo");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "nSpecialDaysSchedule");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "stuValidBeginTime");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "bFirstEnter");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "nFirstEnterDoors");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "pstuFloorsEx2");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "pstuUserInfoEx");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "stuAllowCheckInTime");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "pstuUserInfoEx3");
                PrintOffset(typeof(NET_ACCESS_USER_INFO), "byReserved");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error while dumping sizes/offsets: " + ex);
            }

            Console.WriteLine("--- End Interop Diagnostics ---");
        }

        private static void PrintOffset(Type t, string fieldName)
        {
            try
            {
                var off = Marshal.OffsetOf(t, fieldName);
                Console.WriteLine($"{fieldName} offset = {off}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not get offset for {fieldName}: {ex.Message}");
            }
        }

        // Add optional channel parameter so callers can select which camera/channel
        public bool Login(
            string ip,
            int port,
            string username,
            string password,
            int recognitionChannel = 0)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException(
                    "IP address is required.",
                    nameof(ip));

            if (port <= 0 || port > 65535)
                throw new ArgumentException(
                    "Port must be between 1 and 65535.",
                    nameof(port));

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException(
                    "Username is required.",
                    nameof(username));

            password ??= string.Empty;

            if (!_initialized)
            {
                if (!Initialize())
                    return false;
            }

            if (_loginHandle != 0)
            {
                Logout();
            }

            NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY loginIn =
                new NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY
                {
                    dwSize =
                        (uint)Marshal.SizeOf<
                            NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY>(),

                    szIP = ip,
                    nPort = port,
                    szUserName = username,
                    szPassword = password,

                    emSpecCap = 0,

                    byReserved = new byte[4],

                    pCapParam = IntPtr.Zero,

                    emTLSCap = 0,

                    szLocalIP = string.Empty,

                    // Windows
                    nClientType = 3
                };

            NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY loginOut =
                new NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY
                {
                    dwSize =
                        (uint)Marshal.SizeOf<
                            NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY>(),

                    stuDeviceInfo =
                        CreateDeviceInfo(),

                    nError = 0,

                    byReserved = new byte[132]
                };

            try
            {
            Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Entering Login() _loginHandle={_loginHandle}");

                long loginHandle =
                    CLIENT_LoginWithHighLevelSecurity(
                        ref loginIn,
                        ref loginOut);

                uint sdkError =
                    SafeGetLastError();

                Console.WriteLine("--------------------------------");
                Console.WriteLine("DAHUA LOGIN");
                Console.WriteLine($"IP           : {ip}");
                Console.WriteLine($"Port         : {port}");
                Console.WriteLine($"Username     : {username}");
                Console.WriteLine($"Login Handle : {loginHandle}");
                Console.WriteLine($"Output Error : {loginOut.nError}");
                Console.WriteLine($"SDK Error    : {sdkError}");
                Console.WriteLine("--------------------------------");

                if (loginHandle == 0)
                {
                    Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Login failed: native returned 0. STACK:{Environment.StackTrace}");
                    _loginHandle = 0;
                    return false;
                }

                _loginHandle = loginHandle;
                Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Login succeeded. _loginHandle set to {_loginHandle}");

                _lastLoginIp = ip;
                _lastLoginPort = port;
                _lastLoginUsername = username;
                _lastLoginPassword = password;
                SaveSessionState();

                // store requested recognition channel for later subscription
                _recognitionChannel = recognitionChannel;

                // Diagnostic: attempt to subscribe to device alarm/messages
                try
                {
                    bool startResult = CLIENT_StartListen(_loginHandle);
                    uint startErr = SafeGetLastError();
                    _startListenActive = startResult;
                    Console.WriteLine($"[DahuaSDK] StartListen result={startResult} loginHandle={_loginHandle} sdkError={startErr} hexError=0x{startErr:X8}");

                    // Diagnostic: attempt extended alarm subscription (StartListenEx)
                    try
                    {
                        bool startExResult = CLIENT_StartListenEx(_loginHandle);
                        uint startExErr = SafeGetLastError();
                        _startListenExActive = startExResult;
                        Console.WriteLine($"[DahuaSDK] StartListenEx result={startExResult} loginHandle={_loginHandle} sdkError={startExErr} hexError=0x{startExErr:X8}");
                    }
                    catch (Exception exEx)
                    {
                        Console.WriteLine($"[DahuaSDK] StartListenEx exception: {exEx}");
                        _startListenExActive = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DahuaSDK] StartListen exception: {ex}");
                    _startListenActive = false;
                }

                AttachRecognitionEvents();

                // Diagnostic: query recent face-recognition records (short window)
                // NOTE: This diagnostic uses complex native query paths that exercise
                // START/DO/STOP multi-find native APIs. These code paths have caused
                // native heap corruption on some devices when used without full
                // struct parity for FACERECOGNITION_PERSON_INFOEX. Because this
                // diagnostic is optional, run it only when explicitly enabled via
                // environment variable DAHUA_ENABLE_FACE_RECORD_DIAGNOSTIC=1. By
                // default it is disabled to avoid native crashes in production.
                try
                {
                    var diagFlag = Environment.GetEnvironmentVariable("DAHUA_ENABLE_FACE_RECORD_DIAGNOSTIC");
                    if (!string.IsNullOrEmpty(diagFlag) && diagFlag == "1")
                    {
                        RunFaceRecordDiagnostic();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] exception: {ex}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Dahua login exception: {ex}");
            Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Login exception - clearing _loginHandle. STACK:{Environment.StackTrace}");

            _loginHandle = 0;

            return false;
            }
        }

        // ============================================================
        // Legacy Attendance Users
        // ============================================================

        public AttendanceUsersResult FindAttendanceUsers(
            int offset = 0,
            int count = 10)
        {
            if (!IsLoggedIn)
            {
                return FailUsers(
                    "Device is not logged in.");
            }

            offset = Math.Max(offset, 0);

            if (count <= 0)
                count = 10;

            count = Math.Min(
                count,
                MAX_LEGACY_USERS_PER_QUERY);

            IntPtr userInfoBuffer = IntPtr.Zero;

            try
            {
                int inputSize =
                    Marshal.SizeOf<
                        NET_IN_ATTENDANCE_FINDUSER>();

                int outputSize =
                    Marshal.SizeOf<
                        NET_OUT_ATTENDANCE_FINDUSER>();

                int userInfoSize =
                    Marshal.SizeOf<
                        NET_ATTENDANCE_USERINFO>();

                long bufferSize =
                    checked((long)userInfoSize * count);

                if (bufferSize <= 0 ||
                    bufferSize > int.MaxValue)
                {
                    return FailUsers(
                        "Legacy attendance user buffer size is invalid.");
                }

                userInfoBuffer =
                    Marshal.AllocHGlobal(
                        new IntPtr(bufferSize));

                ZeroMemory(
                    userInfoBuffer,
                    checked((int)bufferSize));

                NET_IN_ATTENDANCE_FINDUSER input =
                    new NET_IN_ATTENDANCE_FINDUSER
                    {
                        dwSize = (uint)inputSize,
                        nOffset = offset,
                        nPagedQueryCount = count
                    };

                NET_OUT_ATTENDANCE_FINDUSER output =
                    new NET_OUT_ATTENDANCE_FINDUSER
                    {
                        dwSize = (uint)outputSize,
                        nTotalUser = 0,
                        nMaxUserCount = count,
                        stuUserInfo = userInfoBuffer,
                        nRetUserCount = 0,
                        nMaxPhotoDataLength = 0,
                        nRetPhoteLength = 0,
                        pbyPhotoData = IntPtr.Zero
                    };

                bool result =
                    CLIENT_Attendance_FindUser(
                        _loginHandle,
                        ref input,
                        ref output,
                        SDK_TIMEOUT);

                uint sdkError =
                    SafeGetLastError();

                if (!result)
                {
                    return new AttendanceUsersResult
                    {
                        Success = false,

                        Message =
                            "CLIENT_Attendance_FindUser failed. " +
                            $"SDK Error: {sdkError}",

                        TotalUsers =
                            Math.Max(output.nTotalUser, 0),

                        ReturnedUsers = 0,

                        Users =
                            new List<AttendanceUser>()
                    };
                }

                int returnedCount =
                    Math.Min(
                        Math.Max(
                            output.nRetUserCount,
                            0),
                        count);

                List<AttendanceUser> users =
                    new List<AttendanceUser>(
                        returnedCount);

                for (int i = 0;
                     i < returnedCount;
                     i++)
                {
                    IntPtr currentPtr =
                        IntPtr.Add(
                            userInfoBuffer,
                            checked(i * userInfoSize));

                    NET_ATTENDANCE_USERINFO nativeUser =
                        Marshal.PtrToStructure<
                            NET_ATTENDANCE_USERINFO>(
                                currentPtr);

                    users.Add(
                        new AttendanceUser
                        {
                            UserId =
                                ReadAnsiString(
                                    nativeUser.szUserID),

                            UserName =
                                ReadAnsiString(
                                    nativeUser.szUserName),

                            CardNo =
                                ReadAnsiString(
                                    nativeUser.szCardNo),

                            Password =
                                ReadAnsiString(
                                    nativeUser.szPassword),

                            ClassNumber =
                                ReadAnsiString(
                                    nativeUser.szClassNumber),

                            PhoneNumber =
                                ReadAnsiString(
                                    nativeUser.szPhoneNumber),

                            PhotoLength =
                                nativeUser.nPhotoLength
                        });
                }

                return new AttendanceUsersResult
                {
                    Success = true,

                    Message =
                        "Users retrieved successfully.",

                    TotalUsers =
                        Math.Max(
                            output.nTotalUser,
                            returnedCount),

                    ReturnedUsers =
                        returnedCount,

                    Users = users
                };
            }
            catch (Exception ex)
            {
                return FailUsers(
                    $"Attendance user search exception: {ex.Message}");
            }
            finally
            {
                FreeHGlobal(
                    ref userInfoBuffer);
            }
        }

        // ============================================================
        // Modern Access Control Users
        // ============================================================

        public AttendanceUsersResult FindAccessControlUsers(
            int offset = 0,
            int count = 10)
        {
            if (!IsLoggedIn)
            {
                return FailUsers(
                    "Device is not logged in.");
            }

            offset = Math.Max(offset, 0);

            if (count <= 0)
                count = 10;

            count = Math.Min(
                count,
                MAX_ACCESS_USERS_PER_QUERY);

            long findHandle = 0;
            IntPtr userInfoBuffer = IntPtr.Zero;

            try
            {
                NET_IN_USERINFO_START_FIND startIn =
                    new NET_IN_USERINFO_START_FIND
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_IN_USERINFO_START_FIND>(),

                        szUserID = string.Empty,

                        szPassword = string.Empty,

                        bQueryPWDNotEmpty = 0
                    };

                NET_OUT_USERINFO_START_FIND startOut =
                    new NET_OUT_USERINFO_START_FIND
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_OUT_USERINFO_START_FIND>(),

                        nTotalCount = 0,

                        nCapNum = 0
                    };

                findHandle =
                    CLIENT_StartFindUserInfo(
                        _loginHandle,
                        ref startIn,
                        ref startOut,
                        FIND_TIMEOUT);

                uint startError =
                    SafeGetLastError();

                if (findHandle == 0)
                {
                    return FailUsers(
                        "CLIENT_StartFindUserInfo failed. " +
                        $"SDK Error: {startError}");
                }

                int userInfoSize =
                    Marshal.SizeOf<
                        NET_ACCESS_USER_INFO>();

                // Native SDK header has natural alignment.
                // The expected native size is derived from the SDK header
                // included in SDK/Include/Common/dhnetsdk.h for x64 builds.
                // For the SDK version shipped with this project the correct
                // x64 size is 6840 bytes. Validate managed layout against
                // that size to catch accidental ABI drift.
                if (IntPtr.Size == 8 &&
                    userInfoSize != 6840)
                {
                    return FailUsers(
                        "NET_ACCESS_USER_INFO native layout mismatch. " +
                        $"Managed size={userInfoSize}, expected x64 size=6840.");
                }

                long totalBufferSize =
                    checked((long)userInfoSize * count);

                if (totalBufferSize <= 0 ||
                    totalBufferSize > int.MaxValue)
                {
                    return FailUsers(
                        "Access control user buffer size is invalid.");
                }

                userInfoBuffer =
                    Marshal.AllocHGlobal(
                        new IntPtr(totalBufferSize));

                ZeroMemory(
                    userInfoBuffer,
                    checked((int)totalBufferSize));

                NET_IN_USERINFO_DO_FIND doIn =
                    new NET_IN_USERINFO_DO_FIND
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_IN_USERINFO_DO_FIND>(),

                        nStartNo = offset,

                        nCount = count
                    };

                NET_OUT_USERINFO_DO_FIND doOut =
                    new NET_OUT_USERINFO_DO_FIND
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_OUT_USERINFO_DO_FIND>(),

                        nRetNum = 0,

                        pstuInfo =
                            userInfoBuffer,

                        nMaxNum = count,

                        byReserved = new byte[4]
                    };

                bool result =
                    CLIENT_DoFindUserInfo(
                        findHandle,
                        ref doIn,
                        ref doOut,
                        FIND_TIMEOUT);

                uint sdkError =
                    SafeGetLastError();

                if (!result)
                {
                    return new AttendanceUsersResult
                    {
                        Success = false,

                        Message =
                            "CLIENT_DoFindUserInfo failed. " +
                            $"SDK Error: {sdkError}",

                        TotalUsers =
                            Math.Max(
                                startOut.nTotalCount,
                                0),

                        ReturnedUsers = 0,

                        Users =
                            new List<AttendanceUser>()
                    };
                }

                int returnedCount =
                    Math.Min(
                        Math.Max(
                            doOut.nRetNum,
                            0),
                        count);

                List<AttendanceUser> users =
                    new List<AttendanceUser>(
                        returnedCount);

                for (int i = 0;
                     i < returnedCount;
                     i++)
                {
                    IntPtr currentPtr =
                        IntPtr.Add(
                            userInfoBuffer,
                            checked(i * userInfoSize));

                    NET_ACCESS_USER_INFO nativeUser =
                        Marshal.PtrToStructure<
                            NET_ACCESS_USER_INFO>(
                                currentPtr);

                    users.Add(
                        new AttendanceUser
                        {
                            UserId =
                                ReadAnsiString(
                                    nativeUser.szUserID),

                            UserName =
                                ReadAnsiString(
                                    nativeUser.szName),

                            CardNo =
                                ReadAnsiString(
                                    nativeUser.szCitizenIDNo),

                            Password =
                                ReadAnsiString(
                                    nativeUser.szPsw),

                            ClassNumber =
                                ReadAnsiString(
                                    nativeUser.szClassInfo),

                            PhoneNumber =
                                ReadAnsiString(
                                    nativeUser.szPhoneNumber),

                            PhotoLength = 0
                        });
                }

                return new AttendanceUsersResult
                {
                    Success = true,

                    Message =
                        "Access control users retrieved successfully.",

                    TotalUsers =
                        Math.Max(
                            startOut.nTotalCount,
                            returnedCount),

                    ReturnedUsers =
                        returnedCount,

                    Users = users
                };
            }
            catch (Exception ex)
            {
                return FailUsers(
                    "Access control user search exception: " +
                    ex.Message);
            }
            finally
            {
                FreeHGlobal(
                    ref userInfoBuffer);

                if (findHandle != 0)
                {
                    try
                    {
                        CLIENT_StopFindUserInfo(
                            findHandle);
                    }
                    catch
                    {
                        // Never throw from finally.
                    }
                }
            }
        }

        // ============================================================
        // CREATE ACCESS CONTROL USER
        // ============================================================

        public CreateUserResult CreateAccessControlUser(
            string userId,
            string name,
            CreateAccessControlUserOptions? opts = null)
        {
            if (!EnsureLoggedIn())
            {
                return FailCreateUser(
                    "Device is not logged in. Log in to the Dahua terminal via the Device page once so the backend can reconnect automatically.");
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                return FailCreateUser(
                    "userId is required.");
            }

            userId = userId.Trim();

            if (Encoding.ASCII.GetByteCount(userId) >=
                MAX_USER_ID_BYTES)
            {
                return FailCreateUser(
                    "userId must fit in the Dahua 32-byte user ID field.");
            }

            if (string.IsNullOrWhiteSpace(name))
                name = userId;

            name = name.Trim();

            if (Encoding.ASCII.GetByteCount(name) >=
                MAX_USER_NAME_BYTES)
            {
                name = TruncateAscii(
                    name,
                    MAX_USER_NAME_BYTES - 1);
            }

            IntPtr userInfoPtr = IntPtr.Zero;
            IntPtr inParamPtr = IntPtr.Zero;
            IntPtr outParamPtr = IntPtr.Zero;
            IntPtr failCodePtr = IntPtr.Zero;

            try
            {
                // ----------------------------------------------------
                // IMPORTANT:
                //
                // Native SDK uses:
                //
                // int nDoors[...]
                // int nTimeSectionNo[...]
                // int nSpecialDaysSchedule[...]
                // int nFirstEnterDoors[...]
                //
                // They MUST be int[] in managed interop.
                // ----------------------------------------------------

                NET_ACCESS_USER_INFO userInfo =
                    CreateEmptyAccessUserInfo();

                userInfo.szUserID =
                    ToFixedAnsiBytes(
                        userId,
                        32);

                userInfo.szName =
                    ToFixedAnsiBytes(
                        name,
                        32);

                // Normal user (default)
                userInfo.emUserType = 0;

                // Normal status
                userInfo.nUserStatus = 0;

                // Guest/user time
                userInfo.nUserTime = 0;

                // No card/password by default.
                userInfo.szCitizenIDNo =
                    new byte[32];

                userInfo.szPsw =
                    new byte[64];

                // ----------------------------------------------------
                // Door/time permissions
                // ----------------------------------------------------

                userInfo.nDoorNum = 0;

                // By default use one time section (255 = all time)
                userInfo.nTimeSectionNum = 1;
                userInfo.nTimeSectionNo[0] = 255;

                userInfo.nSpecialDaysScheduleNum = 1;
                userInfo.nSpecialDaysSchedule[0] = 255;

                // Apply options if provided. Only map fields where the SDK representation is
                // straightforward and does not require guessing numeric enum values.
                if (opts != null)
                {
                    // Department: store as ASCII string in szDepartment
                    if (opts.Department.HasValue)
                    {
                        var depStr = opts.Department.Value.ToString();
                        userInfo.szDepartment = ToFixedAnsiBytes(depStr, userInfo.szDepartment.Length);
                    }

                    // Period/time-section: map to nTimeSectionNo[0]
                    if (opts.Period.HasValue)
                    {
                        userInfo.nTimeSectionNum = 1;
                        userInfo.nTimeSectionNo[0] = opts.Period.Value;
                    }

                    // Holiday plan / special days schedule
                    if (opts.HolidayPlan.HasValue)
                    {
                        userInfo.nSpecialDaysScheduleNum = 1;
                        userInfo.nSpecialDaysSchedule[0] = opts.HolidayPlan.Value;
                    }

                    // Validity period
                    if (opts.ValidFrom.HasValue)
                    {
                        userInfo.stuValidBeginTime = ToNetTime(opts.ValidFrom.Value);
                    }
                    if (opts.ValidTo.HasValue)
                    {
                        userInfo.stuValidEndTime = ToNetTime(opts.ValidTo.Value);
                    }

                    // Note: emAuthority (Permission) and emUserType (UserType) are intentionally
                    // left as defaults unless explicit numeric mappings are provided, to avoid
                    // incorrect behavior across device firmware versions.
                }

                // ----------------------------------------------------
                // Validity
                // ----------------------------------------------------

                DateTime now =
                    DateTime.UtcNow;

                DateTime end =
                    now.AddYears(10);

                userInfo.stuValidBeginTime =
                    ToNetTime(now);

                userInfo.stuValidEndTime =
                    ToNetTime(end);

                // ----------------------------------------------------
                // First enter
                // ----------------------------------------------------

                userInfo.bFirstEnter = 0;
                userInfo.nFirstEnterDoorsNum = 0;

                // ----------------------------------------------------
                // Authority
                //
                // SDK demo/default:
                // customer/normal user = 0
                // ----------------------------------------------------

                userInfo.emAuthority = 0;

                userInfo.nRepeatEnterRouteTimeout = 0;

                // No floors/rooms.
                userInfo.nFloorNum = 0;
                userInfo.nRoom = 0;

                // No extended floor data.
                userInfo.bFloorNoExValid = 0;
                userInfo.nFloorNumEx = 0;

                // No birth date.
                userInfo.stuBirthDay =
                    new NET_TIME();

                // Default values.
                userInfo.emSex = 0;

                userInfo.bFloorNoEx2Valid = 0;
                userInfo.pstuFloorsEx2 = IntPtr.Zero;

                userInfo.bHealthStatus = 0;

                // ----------------------------------------------------
                // Extended user-time fields
                // ----------------------------------------------------

                userInfo.nUserTimeSectionsNum = 0;

                userInfo.emTypeOfCertificate = 0;

                userInfo.nSignNum = 0;

                userInfo.stuStartTimeInPeriodOfValidity =
                    new NET_TIME();

                userInfo.emTestItems = 0;

                userInfo.bUseNameEx = 0;
                userInfo.bUserInfoExValid = 0;
                userInfo.pstuUserInfoEx = IntPtr.Zero;

                userInfo.nAuthOverdueTime = 0;

                userInfo.emGreenCNHealthStatus = 0;
                userInfo.emAllowPermitFlag = 0;
                userInfo.nHolidayGroupIndex = 0;

                userInfo.stuUpdateTime =
                    ToNetTime(DateTime.UtcNow);

                userInfo.nValidFromsNum = 0;
                userInfo.nValidTosNum = 0;

                userInfo.bUserIDEx = 0;

                userInfo.nFinancialUserType = 0;
                userInfo.nCustomUserType = 0;
                userInfo.nCustomUserTypeValue = 0;

                userInfo.stuAllowCheckInTime =
                    new NET_TIME_EX();

                userInfo.stuAllowCheckOutTime =
                    new NET_TIME_EX();

                userInfo.pstuUserInfoEx2 =
                    IntPtr.Zero;

                userInfo.bUserInfoEx2Valid = 0;

                userInfo.nRoleID = 0;

                userInfo.pstuUserInfoEx3 =
                    IntPtr.Zero;

                userInfo.bUserInfoEx3Valid = 0;

                // ----------------------------------------------------
                // Native layout check
                // ----------------------------------------------------

                int userInfoSize =
                    Marshal.SizeOf<
                        NET_ACCESS_USER_INFO>();

                if (IntPtr.Size == 8 &&
                    userInfoSize != 6840)
                {
                    return FailCreateUser(
                        "NET_ACCESS_USER_INFO layout mismatch. " +
                        $"Managed={userInfoSize}, expected x64=6840.");
                }

                // ----------------------------------------------------
                // Allocate user struct
                // ----------------------------------------------------

                userInfoPtr =
                    Marshal.AllocHGlobal(
                        userInfoSize);

                ZeroMemory(
                    userInfoPtr,
                    userInfoSize);

                Marshal.StructureToPtr(
                    userInfo,
                    userInfoPtr,
                    false);

                // ----------------------------------------------------
                // Input
                // ----------------------------------------------------

                NET_IN_ACCESS_USER_SERVICE_INSERT inParam =
                    new NET_IN_ACCESS_USER_SERVICE_INSERT
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_IN_ACCESS_USER_SERVICE_INSERT>(),

                        nInfoNum = 1,

                        pUserInfo =
                            userInfoPtr
                    };

                int inParamSize =
                    Marshal.SizeOf<
                        NET_IN_ACCESS_USER_SERVICE_INSERT>();

                inParamPtr =
                    Marshal.AllocHGlobal(
                        inParamSize);

                ZeroMemory(
                    inParamPtr,
                    inParamSize);

                Marshal.StructureToPtr(
                    inParam,
                    inParamPtr,
                    false);

                // ----------------------------------------------------
                // Fail code
                //
                // NET_EM_FAILCODE is a native enum => 4-byte int.
                // ----------------------------------------------------

                failCodePtr =
                    Marshal.AllocHGlobal(
                        sizeof(int));

                Marshal.WriteInt32(
                    failCodePtr,
                    0);

                // ----------------------------------------------------
                // Output
                // ----------------------------------------------------

                NET_OUT_ACCESS_USER_SERVICE_INSERT outParam =
                    new NET_OUT_ACCESS_USER_SERVICE_INSERT
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_OUT_ACCESS_USER_SERVICE_INSERT>(),

                        nMaxRetNum = 1,

                        pFailCode =
                            failCodePtr
                    };

                int outParamSize =
                    Marshal.SizeOf<
                        NET_OUT_ACCESS_USER_SERVICE_INSERT>();

                outParamPtr =
                    Marshal.AllocHGlobal(
                        outParamSize);

                ZeroMemory(
                    outParamPtr,
                    outParamSize);

                Marshal.StructureToPtr(
                    outParam,
                    outParamPtr,
                    false);

                // ----------------------------------------------------
                // SDK call
                // ----------------------------------------------------

                bool result =
                    CLIENT_OperateAccessUserService(
                        _loginHandle,
                        NET_EM_ACCESS_CTL_USER_SERVICE_INSERT,
                        inParamPtr,
                        outParamPtr,
                        SDK_TIMEOUT);

                uint sdkError =
                    SafeGetLastError();

                int failCode =
                    Marshal.ReadInt32(
                        failCodePtr);

                Console.WriteLine(
                    $"[DahuaSDK DIAG] AFTER CLIENT_OperateAccessFaceService " +
                    $"result={result} " +
                    $"sdkError={sdkError} " +
                    $"failCode={failCode}");

                string reason =
                    DescribeUserFailCode(
                        failCode);

                Console.WriteLine("--------------------------------");
                Console.WriteLine("DAHUA CREATE ACCESS USER");
                Console.WriteLine($"User ID       : {userId}");
                Console.WriteLine($"Name          : {name}");
                Console.WriteLine($"Struct Size   : {userInfoSize}");
                Console.WriteLine($"Call Result   : {result}");
                Console.WriteLine($"SDK Error     : {sdkError}");
                Console.WriteLine($"Fail Code     : {failCode}");
                Console.WriteLine($"Fail Reason   : {reason}");
                Console.WriteLine("--------------------------------");

                if (!result)
                {
                    return new CreateUserResult
                    {
                        Success = false,

                        Message =
                            "CLIENT_OperateAccessUserService failed. " +
                            $"SDK Error: {sdkError}; " +
                            $"Fail Code: {failCode}; " +
                            $"Reason: {reason}",

                        FailCode = failCode,

                        SdkError = sdkError
                    };
                }

                if (failCode != 0)
                {
                    return new CreateUserResult
                    {
                        Success = false,

                        Message =
                            "Device rejected user creation. " +
                            $"Fail Code: {failCode}; " +
                            $"Reason: {reason}",

                        FailCode = failCode,

                        SdkError = sdkError
                    };
                }

                return new CreateUserResult
                {
                    Success = true,

                    Message =
                        "Access control user created successfully.",

                    FailCode = 0,

                    SdkError = sdkError
                };
            }
            catch (Exception ex)
            {
                uint sdkError =
                    SafeGetLastError();

                Console.WriteLine(
                    "CREATE ACCESS USER EXCEPTION");

                Console.WriteLine(ex);

                return new CreateUserResult
                {
                    Success = false,

                    Message =
                        "CreateAccessControlUser exception: " +
                        ex.Message,

                    FailCode = -1,

                    SdkError = sdkError
                };
            }
            finally
            {
                FreeHGlobal(
                    ref outParamPtr);

                FreeHGlobal(
                    ref inParamPtr);

                FreeHGlobal(
                    ref userInfoPtr);

                FreeHGlobal(
                    ref failCodePtr);
            }
        }

        // ============================================================
        // DELETE ACCESS CONTROL USER
        // ============================================================

        public DeleteUserResult DeleteAccessControlUser(
            string userId)
        {
            if (!EnsureLoggedIn())
            {
                return new DeleteUserResult
                {
                    Success = false,
                    Message = "Device is not logged in. Log in to the Dahua terminal via the Device page once so the backend can reconnect automatically.",
                    FailCode = -1,
                    SdkError = 0
                };
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                return new DeleteUserResult
                {
                    Success = false,
                    Message = "userId is required.",
                    FailCode = -1,
                    SdkError = 0
                };
            }

            userId = userId.Trim();

            if (Encoding.ASCII.GetByteCount(userId) >= MAX_USER_ID_BYTES)
            {
                return new DeleteUserResult
                {
                    Success = false,
                    Message = "userId must fit in the Dahua 32-byte user ID field.",
                    FailCode = -1,
                    SdkError = 0
                };
            }

            IntPtr inParamPtr = IntPtr.Zero;
            IntPtr outParamPtr = IntPtr.Zero;
            IntPtr failCodePtr = IntPtr.Zero;

            try
            {
                // Build managed in-struct and flatten user id into the flattened array
                NET_IN_ACCESS_USER_SERVICE_REMOVE inParam = new NET_IN_ACCESS_USER_SERVICE_REMOVE
                {
                    dwSize = (uint)Marshal.SizeOf<NET_IN_ACCESS_USER_SERVICE_REMOVE>(),
                    nUserNum = 1,
                    szUserID = new byte[100 * 32],
                    szUserIDEx = new byte[100 * 128],
                    bUserIDEx = 0
                };

                var uidBytes = Encoding.Default.GetBytes(userId);
                int copyLen = Math.Min(uidBytes.Length, 31);
                Array.Copy(uidBytes, 0, inParam.szUserID, 0, copyLen);

                int inParamSize = Marshal.SizeOf<NET_IN_ACCESS_USER_SERVICE_REMOVE>();
                inParamPtr = Marshal.AllocHGlobal(inParamSize);
                ZeroMemory(inParamPtr, inParamSize);
                Marshal.StructureToPtr(inParam, inParamPtr, false);

                failCodePtr = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(failCodePtr, 0);

                NET_OUT_ACCESS_USER_SERVICE_REMOVE outParam = new NET_OUT_ACCESS_USER_SERVICE_REMOVE
                {
                    dwSize = (uint)Marshal.SizeOf<NET_OUT_ACCESS_USER_SERVICE_REMOVE>(),
                    nMaxRetNum = 1,
                    pFailCode = failCodePtr
                };

                int outParamSize = Marshal.SizeOf<NET_OUT_ACCESS_USER_SERVICE_REMOVE>();
                outParamPtr = Marshal.AllocHGlobal(outParamSize);
                ZeroMemory(outParamPtr, outParamSize);
                Marshal.StructureToPtr(outParam, outParamPtr, false);

                bool result = CLIENT_OperateAccessUserService(
                    _loginHandle,
                    NET_EM_ACCESS_CTL_USER_SERVICE_REMOVE,
                    inParamPtr,
                    outParamPtr,
                    SDK_TIMEOUT);

                uint sdkError = SafeGetLastError();
                int failCode = Marshal.ReadInt32(failCodePtr);

                if (!result)
                {
                    return new DeleteUserResult
                    {
                        Success = false,
                        Message = $"CLIENT_OperateAccessUserService(REMOVE) failed. SDK Error: {sdkError}; Fail Code: {failCode}",
                        FailCode = failCode,
                        SdkError = sdkError
                    };
                }

                if (failCode != 0)
                {
                    return new DeleteUserResult
                    {
                        Success = false,
                        Message = $"Device rejected delete. Fail Code: {failCode}.",
                        FailCode = failCode,
                        SdkError = sdkError
                    };
                }

                return new DeleteUserResult
                {
                    Success = true,
                    Message = "Access control user removed successfully.",
                    FailCode = 0,
                    SdkError = sdkError
                };
            }
            catch (Exception ex)
            {
                uint sdkError = SafeGetLastError();
                return new DeleteUserResult
                {
                    Success = false,
                    Message = "DeleteAccessControlUser exception: " + ex.Message,
                    FailCode = -1,
                    SdkError = sdkError
                };
            }
            finally
            {
                FreeHGlobal(ref outParamPtr);
                FreeHGlobal(ref inParamPtr);
                FreeHGlobal(ref failCodePtr);
            }
        }

        // ============================================================
        // ENROLL FACE
        // ============================================================

        public FaceEnrollResult EnrollFace(
            string userId,
            byte[] photoJpegBytes)
        {
            if (!EnsureLoggedIn())
            {
                return FailFace(
                    "Device is not logged in. Log in to the Dahua terminal via the Device page once so the backend can reconnect automatically.");
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                return FailFace(
                    "userId is required.");
            }

            userId = userId.Trim();

            if (Encoding.ASCII.GetByteCount(userId) >= 32)
            {
                return FailFace(
                    "userId must fit in the Dahua 32-byte user ID field.");
            }

            if (photoJpegBytes == null ||
                photoJpegBytes.Length == 0)
            {
                return FailFace(
                    "JPEG photo data is required.");
            }

            if (photoJpegBytes.Length >
                MAX_FACE_PHOTO_SIZE)
            {
                return FailFace(
                    $"Face photo is too large. " +
                    $"Maximum size is {MAX_FACE_PHOTO_SIZE / 1024} KB.");
            }

            if (!LooksLikeJpeg(photoJpegBytes))
            {
                return FailFace(
                    "The supplied photo does not appear to be a valid JPEG.");
            }

            IntPtr photoPtr = IntPtr.Zero;
            IntPtr faceInfoPtr = IntPtr.Zero;
            IntPtr inParamPtr = IntPtr.Zero;
            IntPtr outParamPtr = IntPtr.Zero;
            IntPtr failCodePtr = IntPtr.Zero;

            try
            {
                // ----------------------------------------------------
                // Photo memory
                // ----------------------------------------------------

                photoPtr =
                    Marshal.AllocHGlobal(
                        photoJpegBytes.Length);

                Marshal.Copy(
                    photoJpegBytes,
                    0,
                    photoPtr,
                    photoJpegBytes.Length);

                // ----------------------------------------------------
                // Face structure
                // ----------------------------------------------------

                NET_ACCESS_FACE_INFO faceInfo =
                    CreateFaceInfo(
                        userId,
                        photoPtr,
                        photoJpegBytes.Length);

                int faceInfoSize =
                    Marshal.SizeOf<
                        NET_ACCESS_FACE_INFO>();

                // Supplied SDK/header:
                //
                // x64:
                // 43288 bytes
                //
                // The uploaded dhnetsdk.dll is x64.
                if (IntPtr.Size == 8 &&
                    faceInfoSize != 43288)
                {
                    Console.WriteLine(
                        $"[DahuaSDK DIAG] FACE STRUCT SIZE MISMATCH " +
                        $"managed={faceInfoSize} expected={43288}");

                    return new FaceEnrollResult
                    {
                        Success = false,

                        Message =
                            "NET_ACCESS_FACE_INFO layout mismatch. " +
                            $"Managed={faceInfoSize}, expected x64=43288.",

                        FailCode = -1,

                        SdkError = 0
                    };
                }

                faceInfoPtr =
                    Marshal.AllocHGlobal(
                        faceInfoSize);

                ZeroMemory(
                    faceInfoPtr,
                    faceInfoSize);

                Marshal.StructureToPtr(
                    faceInfo,
                    faceInfoPtr,
                    false);

                // ----------------------------------------------------
                // Input
                // ----------------------------------------------------

                NET_IN_ACCESS_FACE_SERVICE_INSERT inParam =
                    new NET_IN_ACCESS_FACE_SERVICE_INSERT
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_IN_ACCESS_FACE_SERVICE_INSERT>(),

                        nFaceInfoNum = 1,

                        pFaceInfo =
                            faceInfoPtr
                    };

                int inParamSize =
                    Marshal.SizeOf<
                        NET_IN_ACCESS_FACE_SERVICE_INSERT>();

                inParamPtr =
                    Marshal.AllocHGlobal(
                        inParamSize);

                ZeroMemory(
                    inParamPtr,
                    inParamSize);

                Marshal.StructureToPtr(
                    inParam,
                    inParamPtr,
                    false);

                // ----------------------------------------------------
                // Fail code
                // ----------------------------------------------------

                failCodePtr =
                    Marshal.AllocHGlobal(
                        sizeof(int));

                Marshal.WriteInt32(
                    failCodePtr,
                    0);

                // ----------------------------------------------------
                // Output
                //
                // Native stuDetail is NET_ERROR_DETAIL,
                // exactly 6144 bytes.
                // ----------------------------------------------------

                NET_OUT_ACCESS_FACE_SERVICE_INSERT outParam =
                    new NET_OUT_ACCESS_FACE_SERVICE_INSERT
                    {
                        dwSize =
                            (uint)Marshal.SizeOf<
                                NET_OUT_ACCESS_FACE_SERVICE_INSERT>(),

                        nMaxRetNum = 1,

                        pFailCode =
                            failCodePtr,

                        stuDetail =
                            CreateEmptyErrorDetail()
                    };

                int outParamSize =
                    Marshal.SizeOf<
                        NET_OUT_ACCESS_FACE_SERVICE_INSERT>();

                outParamPtr =
                    Marshal.AllocHGlobal(
                        outParamSize);

                ZeroMemory(
                    outParamPtr,
                    outParamSize);

                Marshal.StructureToPtr(
                    outParam,
                    outParamPtr,
                    false);

                // ----------------------------------------------------
                // SDK call
                // ----------------------------------------------------

                Console.WriteLine(
                    $"[DahuaSDK DIAG] BEFORE CLIENT_OperateAccessFaceService " +
                    $"loginHandle={_loginHandle} " +
                    $"faceInfoSize={faceInfoSize} " +
                    $"inParamSize={inParamSize} " +
                    $"outParamSize={outParamSize}");

                bool result =
                    CLIENT_OperateAccessFaceService(
                        _loginHandle,
                        NET_EM_ACCESS_CTL_FACE_SERVICE_INSERT,
                        inParamPtr,
                        outParamPtr,
                        SDK_TIMEOUT);

                uint sdkError =
                    SafeGetLastError();

                int failCode =
                    Marshal.ReadInt32(
                        failCodePtr);

                string reason =
                    DescribeFaceFailCode(
                        failCode);

                Console.WriteLine("--------------------------------");
                Console.WriteLine("DAHUA FACE ENROLLMENT");
                Console.WriteLine($"User ID      : {userId}");
                Console.WriteLine(
                    $"Photo Bytes  : {photoJpegBytes.Length}");
                Console.WriteLine(
                    $"Face Struct  : {faceInfoSize}");
                Console.WriteLine(
                    $"Call Result  : {result}");
                Console.WriteLine(
                    $"SDK Error    : {sdkError}");
                Console.WriteLine(
                    $"Fail Code    : {failCode}");
                Console.WriteLine(
                    $"Fail Reason  : {reason}");
                Console.WriteLine("--------------------------------");

                if (!result)
                {
                    return new FaceEnrollResult
                    {
                        Success = false,

                        Message =
                            "CLIENT_OperateAccessFaceService failed. " +
                            $"SDK Error: {sdkError}; " +
                            $"Fail Code: {failCode}; " +
                            $"Reason: {reason}",

                        FailCode = failCode,

                        SdkError = sdkError
                    };
                }

                if (failCode != 0)
                {
                    return new FaceEnrollResult
                    {
                        Success = false,

                        Message =
                            "Device rejected face enrollment. " +
                            $"Fail Code: {failCode}; " +
                            $"Reason: {reason}",

                        FailCode = failCode,

                        SdkError = sdkError
                    };
                }

                return new FaceEnrollResult
                {
                    Success = true,

                    Message =
                        "Face enrolled successfully.",

                    FailCode = 0,

                    SdkError = sdkError
                };
            }
            catch (Exception ex)
            {
                uint sdkError =
                    SafeGetLastError();

                Console.WriteLine(
                    "FACE ENROLLMENT EXCEPTION");

                Console.WriteLine(ex);

                return new FaceEnrollResult
                {
                    Success = false,

                    Message =
                        "Face enrollment exception: " +
                        ex.Message,

                    FailCode = -1,

                    SdkError = sdkError
                };
            }
            finally
            {
                FreeHGlobal(ref photoPtr);
                FreeHGlobal(ref faceInfoPtr);
                FreeHGlobal(ref inParamPtr);
                FreeHGlobal(ref outParamPtr);
                FreeHGlobal(ref failCodePtr);
            }
        }

        // ============================================================
        // LOGOUT
        // ============================================================

        public bool Logout()
        {
            Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Entering Logout() current_handle={_loginHandle} STACK:{Environment.StackTrace}");
            if (_loginHandle == 0)
                return true;

            try
            {
                DetachRecognitionEvents();

                if (_personCollectionAttachHandle != 0)
                {
                    bool detached = CLIENT_DetachAccessPersonCollection(
                        _personCollectionAttachHandle);
                    Console.WriteLine(
                        $"[DahuaSDK] Access person collection notification detach: handle={_personCollectionAttachHandle} result={detached} sdkError={SafeGetLastError()}");
                    _personCollectionAttachHandle = 0;
                }

                long handle =
                    _loginHandle;

                // If we previously started alarm listening, stop it before logout
                try
                {
                    if (_startListenActive && handle != 0)
                    {
                        bool stopRes = CLIENT_StopListen(handle);
                        uint stopErr = SafeGetLastError();
                        Console.WriteLine($"[DahuaSDK] StopListen result={stopRes} loginHandle={handle} sdkError={stopErr} hexError=0x{stopErr:X8}");
                        _startListenActive = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DahuaSDK] StopListen exception: {ex}");
                }

                // If extended listen was started, stop it as well
                try
                {
                    if (_startListenExActive && handle != 0)
                    {
                        bool stopExRes = CLIENT_StopListen(handle);
                        uint stopExErr = SafeGetLastError();
                        Console.WriteLine($"[DahuaSDK] StopListen (for StartListenEx) result={stopExRes} loginHandle={handle} sdkError={stopExErr} hexError=0x{stopExErr:X8}");
                        _startListenExActive = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DahuaSDK] StopListen (StartListenEx) exception: {ex}");
                }

                bool result =
                    CLIENT_Logout(handle);

                uint sdkError =
                    SafeGetLastError();

                Console.WriteLine("--------------------------------");
                Console.WriteLine("DAHUA LOGOUT");
                Console.WriteLine($"Handle : {handle}");
                Console.WriteLine($"Result : {result}");
                Console.WriteLine($"Error  : {sdkError}");
                Console.WriteLine("--------------------------------");

                Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Completed CLIENT_Logout(handle={handle}) result={result} sdkError={sdkError} - clearing _loginHandle. STACK:{Environment.StackTrace}");

                // Never retain an invalid handle after logout attempt.
                Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Clearing _loginHandle=0 at Login failure location. STACK:{Environment.StackTrace}");
                _loginHandle = 0;

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Logout exception: {ex.Message}");

                Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Exception during Login - clearing _loginHandle. STACK:{Environment.StackTrace}");
                _loginHandle = 0;

                return false;
            }
        }

        // ============================================================
        // DIAGNOSTIC: Face recognition history query
        // ============================================================
        private void RunFaceRecordDiagnostic()
        {
            if (_loginHandle == 0)
                return;

            try
            {
                Console.WriteLine("[DahuaSDK][FaceRecordDiagnostic] Starting diagnostic query");

                NET_IN_STARTMULTIFIND_FACERECONGNITIONRECORD inStart = new NET_IN_STARTMULTIFIND_FACERECONGNITIONRECORD();
                inStart.dwSize = (uint)Marshal.SizeOf<NET_IN_STARTMULTIFIND_FACERECONGNITIONRECORD>();

                // time window: 2 minutes ago -> now
                DateTime end = DateTime.UtcNow;
                DateTime start = end.AddMinutes(-2);

                inStart.stStartTime = ToNetTime(start);
                inStart.stEndTime = ToNetTime(end);
                inStart.szMachineAddress = string.Empty;
                inStart.nAlarmType = 0;
                inStart.abPersonInfo = 0;
                inStart.pChannelID = IntPtr.Zero;
                inStart.nChannelCount = 0;
                inStart.nGroupIdNum = 0;
                inStart.szGroupId = new byte[128 * 64];
                inStart.abPersonExInfo = 0;
                inStart.stPersonInfoEx = new Dhx_FACERECOGNITION_PERSON_INFO();

                NET_OUT_STARTMULTIFIND_FACERECONGNITIONRECORD outStart = new NET_OUT_STARTMULTIFIND_FACERECONGNITIONRECORD();
                outStart.dwSize = (uint)Marshal.SizeOf<NET_OUT_STARTMULTIFIND_FACERECONGNITIONRECORD>();

                bool startOk = CLIENT_StartMultiFindFaceRecognitionRecord(_loginHandle, ref inStart, ref outStart, 5000);
                uint startErr = SafeGetLastError();
                Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] StartMultiFind result={startOk} findHandle={outStart.lFindHandle} total={outStart.nTotalCount} sdkError={startErr} hexError=0x{startErr:X8}");

                if (!startOk || outStart.lFindHandle == 0)
                    return;

                // Prepare DoFind
                NET_IN_DOFIND_FACERECONGNITIONRECORD inDo = new NET_IN_DOFIND_FACERECONGNITIONRECORD();
                inDo.dwSize = (uint)Marshal.SizeOf<NET_IN_DOFIND_FACERECONGNITIONRECORD>();
                inDo.lFindHandle = outStart.lFindHandle;
                inDo.nBeginNumber = 0;
                inDo.nTotalCount = outStart.nTotalCount > 0 ? outStart.nTotalCount : 20;

                NET_OUT_DOFIND_FACERECONGNITIONRECORD outDo = new NET_OUT_DOFIND_FACERECONGNITIONRECORD();
                outDo.dwSize = (uint)Marshal.SizeOf<NET_OUT_DOFIND_FACERECONGNITIONRECORD>();

                bool doOk = CLIENT_DoFindFaceRecognitionRecord(ref inDo, ref outDo, 5000);
                uint doErr = SafeGetLastError();
                Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] DoFind result={doOk} nResultNum={outDo.nResultNum} nTotalCount={outDo.nTotalCount} sdkError={doErr} hexError=0x{doErr:X8}");

                if (doOk && outDo.nResultNum > 0 && outDo.stuResults != IntPtr.Zero)
                {
                    try
                    {
                        var first = Marshal.PtrToStructure<NET_DOFIND_FACERECONGNITIONRECORD_INFO>(outDo.stuResults);
                        Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] First result: channel={first.nChannelId} candidates={first.nCandidateNum} time={first.stTime.dwYear}-{first.stTime.dwMonth}-{first.stTime.dwDay} {first.stTime.dwHour}:{first.stTime.dwMinute}:{first.stTime.dwSecond}");
                    }
                    catch (Exception mex)
                    {
                        Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] Failed to marshal result: {mex}");
                    }
                }

                // Stop query
                try
                {
                    bool stopRes = CLIENT_StopFindFaceRecognitionRecord(outStart.lFindHandle);
                    uint stopErr = SafeGetLastError();
                    Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] StopFind result={stopRes} findHandle={outStart.lFindHandle} sdkError={stopErr} hexError=0x{stopErr:X8}");
                }
                catch (Exception sex)
                {
                    Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] StopFind exception: {sex}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK][FaceRecordDiagnostic] exception: {ex}");
            }
        }

        // ============================================================
        // Dispose
        // ============================================================

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                Console.WriteLine($"[DahuaSDK TRACE] {DateTime.UtcNow:o} PID={Process.GetCurrentProcess().Id} TID={Thread.CurrentThread.ManagedThreadId} Entering Dispose() _loginHandle={_loginHandle} STACK:{Environment.StackTrace}");
                Logout();

                if (_initialized)
                {
                    try
                    {
                        CLIENT_Cleanup();
                    }
                    catch
                    {
                        // Do not throw during Dispose.
                    }
                }
            }
            finally
            {
                _initialized = false;
                _disposed = true;

                GC.SuppressFinalize(this);
            }
        }

        // ============================================================
        // Device Info
        //
        // IMPORTANT:
        // Native NET_DEVICEINFO_Ex has natural alignment.
        // Pack=1 is intentionally NOT used.
        // ============================================================

        private static NET_DEVICEINFO_Ex CreateDeviceInfo()
        {
            return new NET_DEVICEINFO_Ex
            {
                sSerialNumber = new byte[48],

                bReserved = new byte[2],

                Reserved = new byte[4],

                Reserved2 = new byte[8]
            };
        }

        // ============================================================
        // Empty Access User
        //
        // This fixes the original:
        //
        // "Object reference not set to an instance of an object"
        //
        // because every ByValArray field is explicitly initialized.
        // ============================================================

        private static NET_ACCESS_USER_INFO
            CreateEmptyAccessUserInfo()
        {
            return new NET_ACCESS_USER_INFO
            {
                szUserID = new byte[32],
                szName = new byte[32],

                szCitizenIDNo = new byte[32],
                szPsw = new byte[64],

                // Native int arrays.
                nDoors = new int[32],
                nTimeSectionNo = new int[32],
                nSpecialDaysSchedule = new int[128],
                nFirstEnterDoors = new int[32],

                szFloorNo = new byte[64 * 16],
                szRoomNo = new byte[32 * 16],
                szFloorNoEx = new byte[256 * 4],

                szClassInfo = new byte[256],
                szStudentNo = new byte[64],
                szCitizenAddress = new byte[128],

                szDepartment = new byte[128],
                szSiteCode = new byte[32],
                szPhoneNumber = new byte[32],
                szDefaultFloor = new byte[8],

                szUserTimeSections = new byte[6 * 20],

                szECType = new byte[64],

                szCountryOrAreaCode = new byte[8],
                szCountryOrAreaName = new byte[64],
                szCertificateVersionNumber = new byte[64],
                szApplicationAgencyCode = new byte[64],
                szIssuingAuthority = new byte[64],
                szStartTimeOfCertificateValidity = new byte[64],
                szEndTimeOfCertificateValidity = new byte[64],

                szActualResidentialAddr = new byte[108],
                szWorkClass = new byte[256],

                szNameEx = new byte[128],

                szValidFroms = new byte[8 * 24],
                szValidTos = new byte[8 * 24],

                szUserIDEx = new byte[128],

                szWorkPermitDate = new byte[24],
                szInductionExpireDate = new byte[24],
                szMedicalExpireDate = new byte[24],

                pstuFloorsEx2 = IntPtr.Zero,
                pstuUserInfoEx = IntPtr.Zero,
                pstuUserInfoEx2 = IntPtr.Zero,
                pstuUserInfoEx3 = IntPtr.Zero,

                byReserved = new byte[556]
            };
        }

        // ============================================================
        // Face Info
        // ============================================================

        private static NET_ACCESS_FACE_INFO
            CreateFaceInfo(
                string userId,
                IntPtr photoPtr,
                int photoLength)
        {
            if (photoPtr == IntPtr.Zero)
                throw new ArgumentException(
                    "Photo pointer cannot be null.",
                    nameof(photoPtr));

            if (photoLength <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(photoLength));

            NET_ACCESS_FACE_INFO faceInfo =
                new NET_ACCESS_FACE_INFO
                {
                    szUserID =
                        ToFixedAnsiBytes(
                            userId,
                            32),

                    // No pre-computed feature data.
                    nFaceData = 0,

                    szFaceData =
                        new byte[20 * 2048],

                    nFaceDataLen =
                        new int[20],

                    // One photo.
                    nFacePhoto = 1,

                    nInFacePhotoLen =
                        new int[5],

                    nOutFacePhotoLen =
                        new int[5],

                    pFacePhoto =
                        new IntPtr[5],

                    // Extended face data disabled.
                    bFaceDataExEnable = 0,

                    nMaxFaceDataLen =
                        new int[20],

                    nRetFaceDataLen =
                        new int[20],

                    pFaceDataEx =
                        new IntPtr[20],

                    stuUpdateTime =
                        new NET_TIME(),

                    szUserIDEx =
                        new byte[128],

                    bUserIDEx = 0,

                    // Device extracts face eigen data.
                    nEigenData = 0,

                    nInEigenDataLen =
                        new int[5],

                    nOutEigenDataLen =
                        new int[5],

                    pEigenData =
                        new IntPtr[5],

                    // Native header:
                    //
                    // BYTE byReserved[
                    //     1600 - POINTERSIZE * 5
                    // ];
                    //
                    // Uploaded SDK is x64 => 1560.
                    byReserved =
                        new byte[
                            1600 -
                            (IntPtr.Size * 5)]
                };

            faceInfo.nInFacePhotoLen[0] =
                photoLength;

            faceInfo.nOutFacePhotoLen[0] =
                photoLength;

            faceInfo.pFacePhoto[0] =
                photoPtr;

            return faceInfo;
        }

        // ============================================================
        // Error Detail
        // ============================================================

        private static NET_ERROR_DETAIL
            CreateEmptyErrorDetail()
        {
            return new NET_ERROR_DETAIL
            {
                szExtraInfo =
                    new byte[20 * 256],

                nRetExtraInfoNum = 0,

                szReserved =
                    new byte[1020]
            };
        }

        // ============================================================
        // NET_TIME
        // ============================================================

        private static NET_TIME ToNetTime(
            DateTime value)
        {
            return new NET_TIME
            {
                dwYear = (uint)value.Year,
                dwMonth = (uint)value.Month,
                dwDay = (uint)value.Day,
                dwHour = (uint)value.Hour,
                dwMinute = (uint)value.Minute,
                dwSecond = (uint)value.Second
            };
        }

        // ============================================================
        // JPEG validation
        // ============================================================

        private static bool LooksLikeJpeg(
            byte[] data)
        {
            if (data == null ||
                data.Length < 4)
            {
                return false;
            }

            // JPEG SOI
            if (data[0] != 0xFF ||
                data[1] != 0xD8)
            {
                return false;
            }

            // JPEG EOI.
            //
            // This is a lightweight validation only.
            return
                data[data.Length - 2] == 0xFF &&
                data[data.Length - 1] == 0xD9;
        }

        // ============================================================
        // Fixed ANSI
        // ============================================================

        private static byte[] ToFixedAnsiBytes(
            string value,
            int size)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(size));

            byte[] buffer =
                new byte[size];

            byte[] source =
                Encoding.ASCII.GetBytes(
                    value ?? string.Empty);

            int copyLength =
                Math.Min(
                    source.Length,
                    size - 1);

            if (copyLength > 0)
            {
                Buffer.BlockCopy(
                    source,
                    0,
                    buffer,
                    0,
                    copyLength);
            }

            return buffer;
        }

        // Helper: produce a filesystem-safe name from an input string.
        // Mirrors the controller helper to avoid cross-class dependency.
        private static string MakeSafeName(string input)
        {
            var parts = (input ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries);
            var safe = string.Join("_", parts);
            return string.IsNullOrWhiteSpace(safe) ? "photo" : safe;
        }

        // ============================================================
        // ASCII truncation
        // ============================================================

        private static string TruncateAscii(
            string value,
            int maxBytes)
        {
            if (string.IsNullOrEmpty(value) ||
                maxBytes <= 0)
            {
                return string.Empty;
            }

            byte[] bytes =
                Encoding.ASCII.GetBytes(value);

            if (bytes.Length <= maxBytes)
                return value;

            return Encoding.ASCII.GetString(
                bytes,
                0,
                maxBytes);
        }

        // ============================================================
        // Read native ANSI buffer
        // ============================================================

        private static string ReadAnsiString(
            byte[] value)
        {
            if (value == null ||
                value.Length == 0)
            {
                return string.Empty;
            }

            int length =
                Array.IndexOf(
                    value,
                    (byte)0);

            if (length < 0)
                length = value.Length;

            return Encoding.ASCII.GetString(
                value,
                0,
                length)
                .Trim();
        }

        // ============================================================
        // Zero unmanaged memory
        // ============================================================

        private static void ZeroMemory(
            IntPtr address,
            int size)
        {
            if (address == IntPtr.Zero ||
                size <= 0)
            {
                return;
            }

            byte[] zero =
                new byte[8192];

            int remaining = size;

            IntPtr current =
                address;

            while (remaining > 0)
            {
                int chunk =
                    Math.Min(
                        remaining,
                        zero.Length);

                Marshal.Copy(
                    zero,
                    0,
                    current,
                    chunk);

                current =
                    IntPtr.Add(
                        current,
                        chunk);

                remaining -= chunk;
            }
        }

        // ============================================================
        // Free unmanaged memory safely
        // ============================================================

        private static void FreeHGlobal(
            ref IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
                return;

            try
            {
                Marshal.FreeHGlobal(pointer);
            }
            finally
            {
                pointer = IntPtr.Zero;
            }
        }

        private static byte[] ReadBytes(IntPtr pointer, int length)
        {
            byte[] bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        // ============================================================
        // SDK error safely
        // ============================================================

        private static uint SafeGetLastError()
        {
            try
            {
                return CLIENT_GetLastError();
            }
            catch
            {
                return 0;
            }
        }

        private static string DescribeSdkError(uint error)
        {
            return error switch
            {
                0x80000007 => "NET_ILLEGAL_PARAM (user parameter is illegal)",
                0x80000017 => "NET_NOT_SUPPORTED (SDK function is not supported)",
                0x80000001 => "NET_ERROR (unknown error)",
                0 => "no error reported",
                _ => "unknown SDK error"
            };
        }

        /// <summary>
        /// Public wrapper to obtain a human-friendly description for a Dahua SDK error code.
        /// This lets callers (controllers) include an explanatory string in HTTP responses.
        /// </summary>
        public string GetSdkErrorDescription(uint error)
        {
            return DescribeSdkError(error);
        }

        // ============================================================
        // User fail codes
        //
        // Taken from NET_EM_FAILCODE in supplied Dahua header.
        // ============================================================

        private static string DescribeUserFailCode(
            int code)
        {
            switch (code)
            {
                case 0:
                    return "No error";

                case 1:
                    return "Unknown error";

                case 2:
                    return "Invalid parameter";

                case 3:
                    return "Invalid password";

                case 4:
                    return "Invalid fingerprint";

                case 5:
                    return "Invalid face";

                case 6:
                    return "Invalid card";

                case 7:
                    return "Invalid user";

                case 8:
                    return "Failed to get sub-service";

                case 9:
                    return "Failed to get method";

                case 10:
                    return "Failed to get sub-capabilities";

                case 11:
                    return "Insert limit exceeded";

                case 12:
                    return "Maximum insert rate exceeded";

                case 13:
                    return "Failed to erase fingerprint";

                case 14:
                    return "Failed to erase face";

                case 15:
                    return "Failed to erase card";

                case 16:
                    return "No record";

                case 17:
                    return "No more record";

                case 18:
                    return "Record already exists";

                case 19:
                    return "Maximum fingerprints per user exceeded";

                case 20:
                    return "Maximum cards per user exceeded";

                case 21:
                    return "Maximum photo size exceeded";

                case 22:
                    return "Invalid user ID / user not found";

                case 23:
                    return "Face feature extraction failed";

                case 24:
                    return "Photo already exists";

                case 25:
                    return "Maximum photos per user exceeded";

                case 26:
                    return "Invalid photo format";

                case 27:
                    return "Administrator limit exceeded";

                case 28:
                    return "Face feature already exists";

                case 29:
                    return "Fingerprint already exists";

                case 30:
                    return "Citizen ID already exists";

                case 31:
                    return "Normal user does not support this operation";

                case 32:
                    return "No face detected";

                case 33:
                    return "Multiple faces detected";

                case 34:
                    return "Picture decoding error";

                case 35:
                    return "Picture quality is too low";

                case 36:
                    return "Result is not recommended";

                case 37:
                    return "Face angle exceeds threshold";

                case 38:
                    return "Face ratio exceeds allowed range";

                case 39:
                    return "Face is over exposed";

                case 40:
                    return "Face is under exposed";

                case 41:
                    return "Brightness imbalance";

                case 42:
                    return "Face confidence is low";

                case 43:
                    return "Face alignment score is low";

                case 44:
                    return "Fragmentary face detected";

                case 45:
                    return "Pupil distance is insufficient";

                case 46:
                    return "Face data download failed";

                case 47:
                    return "Face FFE failed";

                case 48:
                    return "Photo feature extraction failed";

                case 49:
                    return "Face data photo is incomplete";

                case 50:
                    return "Database insert overflow";

                case 51:
                    return "Card does not exist";

                case 52:
                    return "User already exists";

                case 53:
                    return "Card number already exists";

                case 54:
                    return "Fingerprint download failed";

                case 55:
                    return "Account is in use";

                default:
                    return
                        $"Unknown Dahua fail code ({code})";
            }
        }

        // ============================================================
        // Face fail codes
        // ============================================================

        private static string DescribeFaceFailCode(
            int code)
        {
            return DescribeUserFailCode(code);
        }

        // ============================================================
        // Result helpers
        // ============================================================

        private static AttendanceUsersResult FailUsers(
            string message)
        {
            return new AttendanceUsersResult
            {
                Success = false,

                Message = message,

                TotalUsers = 0,

                ReturnedUsers = 0,

                Users =
                    new List<AttendanceUser>()
            };
        }

        private static CreateUserResult FailCreateUser(
            string message)
        {
            return new CreateUserResult
            {
                Success = false,

                Message = message,

                FailCode = -1,

                SdkError = 0
            };
        }

        private static FaceEnrollResult FailFace(
            string message)
        {
            return new FaceEnrollResult
            {
                Success = false,

                Message = message,

                FailCode = -1,

                SdkError = 0
            };
        }

        // ============================================================
        // Dispose guard
        // ============================================================

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(DahuaSdkService));
            }
        }

        // ============================================================
        // Device collection mode
        //
        // NET_EM_CFG_COLLECT_USER_INFO_CFG = 4039
        //
        // SDK header comment:
        //   "Whether the access control equipment enables the acquisition
        //    mode, and whether it enters the mode of acquisition while adding"
        //
        // IMPORTANT: This is a DEVICE-WIDE mode, not a per-user command.
        // There is no SDK function to target a specific UserId for capture.
        // Enabling this mode puts the entire device into self-service
        // collection mode. Disable it again after use.
        //
        // Do NOT call this from concurrent registration requests.
        // ============================================================

        public AccessPersonCollectionCapabilities GetAccessPersonCollectionCapabilities()
        {
            ThrowIfDisposed();

            if (!IsLoggedIn)
            {
                Console.WriteLine("[DahuaSDK] Access person collection capabilities: device not logged in.");
                return new AccessPersonCollectionCapabilities
                {
                    Success = false,
                    SdkError = 0
                };
            }

            int inputSize = Marshal.SizeOf<NET_IN_GET_ACCESS_PERSON_COLLECTION_CAPS>();
            int outputSize = Marshal.SizeOf<NET_OUT_GET_ACCESS_PERSON_COLLECTION_CAPS>();
            IntPtr inputPtr = IntPtr.Zero;
            IntPtr outputPtr = IntPtr.Zero;

            try
            {
                var input = new NET_IN_GET_ACCESS_PERSON_COLLECTION_CAPS
                {
                    dwSize = (uint)inputSize
                };
                var output = new NET_OUT_GET_ACCESS_PERSON_COLLECTION_CAPS
                {
                    dwSize = (uint)outputSize,
                    szCardReaderIDList = new byte[32 * 32],
                    szFingerReaderIDList = new byte[32 * 32]
                };

                inputPtr = Marshal.AllocHGlobal(inputSize);
                outputPtr = Marshal.AllocHGlobal(outputSize);
                ZeroMemory(inputPtr, inputSize);
                ZeroMemory(outputPtr, outputSize);
                Marshal.StructureToPtr(input, inputPtr, false);
                Marshal.StructureToPtr(output, outputPtr, false);

                bool result = CLIENT_GetAccessPersonCollectionCaps(
                    _loginHandle, inputPtr, outputPtr, SDK_TIMEOUT);
                uint sdkError = SafeGetLastError();
                output = Marshal.PtrToStructure<NET_OUT_GET_ACCESS_PERSON_COLLECTION_CAPS>(outputPtr);

                string[] cardReaders = ReadReaderList(output.szCardReaderIDList, output.nCardReaderIDListCount);
                string[] fingerReaders = ReadReaderList(output.szFingerReaderIDList, output.nFingerReaderIDListCount);

                Console.WriteLine(
                    "[DahuaSDK] Access person collection capabilities: "
                    + $"result={result} sdkError={sdkError} hexError=0x{sdkError:X8} "
                    + $"mixed={output.bSupportMixedCollection != 0} "
                    + $"typeMask={output.nSupportCollectionType} "
                    + $"face={(output.nSupportCollectionType & 1u) != 0} "
                    + $"idCard={(output.nSupportCollectionType & 2u) != 0} "
                    + $"fingerprint={(output.nSupportCollectionType & 4u) != 0} "
                    + $"card={(output.nSupportCollectionType & 8u) != 0} "
                    + $"iris={(output.nSupportCollectionType & 16u) != 0} "
                    + $"palm={(output.nSupportCollectionType & 32u) != 0} "
                    + $"cardReaders=[{string.Join(",", cardReaders)}] "
                    + $"fingerReaders=[{string.Join(",", fingerReaders)}]");

                long notificationHandle = 0;
                if (result && (output.nSupportCollectionType & 1u) != 0)
                {
                    notificationHandle = AttachAccessPersonCollectionNotifications();
                }

                return new AccessPersonCollectionCapabilities
                {
                    Success = result,
                    SdkError = sdkError,
                    SupportsMixedCollection = output.bSupportMixedCollection != 0,
                    SupportedCollectionTypeMask = output.nSupportCollectionType,
                    CardReaderCount = output.nCardReaderIDListCount,
                    CardReaderIds = cardReaders,
                    FingerReaderCount = output.nFingerReaderIDListCount,
                    FingerReaderIds = fingerReaders,
                    NotificationHandle = notificationHandle
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Access person collection capabilities exception: {ex.Message}");
                return new AccessPersonCollectionCapabilities
                {
                    Success = false,
                    SdkError = SafeGetLastError()
                };
            }
            finally
            {
                FreeHGlobal(ref inputPtr);
                FreeHGlobal(ref outputPtr);
            }
        }

        private static string[] ReadReaderList(byte[]? buffer, int count)
        {
            if (buffer == null || count <= 0)
                return Array.Empty<string>();

            int actualCount = Math.Min(count, 32);
            var readers = new List<string>(actualCount);
            for (int index = 0; index < actualCount; index++)
            {
                byte[] entry = new byte[32];
                Buffer.BlockCopy(buffer, index * 32, entry, 0, 32);
                string value = ReadAnsiString(entry);
                if (!string.IsNullOrWhiteSpace(value))
                    readers.Add(value);
            }

            return readers.ToArray();
        }

        private long AttachAccessPersonCollectionNotifications()
        {
            if (_personCollectionAttachHandle != 0)
                return _personCollectionAttachHandle;

            _personCollectionCallback ??= NativePersonCollectionCallback;
            int inputSize = Marshal.SizeOf<NET_IN_ATTACH_ACCESS_PERSON_COLLECTION>();
            int outputSize = Marshal.SizeOf<NET_OUT_ATTACH_ACCESS_PERSON_COLLECTION>();
            IntPtr inputPtr = IntPtr.Zero;
            IntPtr outputPtr = IntPtr.Zero;

            try
            {
                var input = new NET_IN_ATTACH_ACCESS_PERSON_COLLECTION
                {
                    dwSize = (uint)inputSize,
                    szResvered = new byte[4],
                    cbNotify = Marshal.GetFunctionPointerForDelegate(_personCollectionCallback),
                    dwUser = 0
                };
                var output = new NET_OUT_ATTACH_ACCESS_PERSON_COLLECTION
                {
                    dwSize = (uint)outputSize
                };

                inputPtr = Marshal.AllocHGlobal(inputSize);
                outputPtr = Marshal.AllocHGlobal(outputSize);
                ZeroMemory(inputPtr, inputSize);
                ZeroMemory(outputPtr, outputSize);
                Marshal.StructureToPtr(input, inputPtr, false);
                Marshal.StructureToPtr(output, outputPtr, false);

                long handle = CLIENT_AttachAccessPersonCollection(
                    _loginHandle, inputPtr, outputPtr, SDK_TIMEOUT);
                uint sdkError = SafeGetLastError();
                Console.WriteLine(
                    $"[DahuaSDK] Access person collection notification attach: handle={handle} sdkError={sdkError} hexError=0x{sdkError:X8}");

                _personCollectionAttachHandle = handle > 0 ? handle : 0;
                return _personCollectionAttachHandle;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Access person collection notification attach exception: {ex.Message}");
                return 0;
            }
            finally
            {
                FreeHGlobal(ref inputPtr);
                FreeHGlobal(ref outputPtr);
            }
        }

        private void NativePersonCollectionCallback(
            long attachHandle,
            IntPtr infoPtr,
            IntPtr binaryDataPtr,
            uint binaryDataLength,
            long user)
        {
            try
            {
                if (infoPtr == IntPtr.Zero)
                    return;

                var info = Marshal.PtrToStructure<NET_NOTIFY_PERSON_COLLECTION_INFO>(infoPtr);
                int count = Math.Clamp(info.nDataInfoNum, 0, 20);
                string userId = ReadAnsiString(info.szUserID);
                string uuid = ReadAnsiString(info.szUUID);
                Console.WriteLine(
                    $"[DahuaSDK] Access person collection notification: handle={attachHandle} userId='{userId}' uuid='{uuid}' dataCount={count} binaryLength={binaryDataLength}");

                for (int index = 0; index < count; index++)
                {
                    var data = info.stuDataInfo[index];
                    Console.WriteLine(
                        $"[DahuaSDK] Access person collection result: userId='{userId}' type={data.nType} offset={data.nOffset} length={data.nLength} errorCode={data.nErrorCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DahuaSDK] Access person collection notification parse exception: {ex.Message}");
            }
        }

        public struct CaptureAccessResult
        {
            public bool Success;
            public uint SdkError;
        }

        public CaptureAccessResult CaptureAccessPersonFaceCollection(
            string userId,
            bool enable,
            int allowTimeSeconds = 120)
        {
            Console.WriteLine($"[DahuaSDK] CaptureAccessPersonFaceCollection ENTRY: userId={userId} enable={enable} allowTimeSeconds={allowTimeSeconds} time={DateTime.UtcNow:o}");
            ThrowIfDisposed();

            if (!IsLoggedIn)
            {
                Console.WriteLine(
                    "[DahuaSDK] CaptureAccessPersonFaceCollection: device not logged in.");
                return new CaptureAccessResult { Success = false, SdkError = 0 };
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                Console.WriteLine(
                    "[DahuaSDK] CaptureAccessPersonFaceCollection: userId is required.");
                return new CaptureAccessResult { Success = false, SdkError = 0 };
            }

            if (allowTimeSeconds < 0)
            {
                Console.WriteLine(
                    "[DahuaSDK] CaptureAccessPersonFaceCollection: allowTimeSeconds cannot be negative.");
                return new CaptureAccessResult { Success = false, SdkError = 0 };
            }

            int inputSize = Marshal.SizeOf<NET_IN_CAPTURE_ACCESS_PERSON_COLLECTION_CMD>();
            int outputSize = Marshal.SizeOf<NET_OUT_CAPTURE_ACCESS_PERSON_COLLECTION_CMD>();
            IntPtr inputPtr = IntPtr.Zero;
            IntPtr outputPtr = IntPtr.Zero;

            try
            {
                var input = new NET_IN_CAPTURE_ACCESS_PERSON_COLLECTION_CMD
                {
                    dwSize = (uint)inputSize,
                    nType = 1,
                    szUUID = ToFixedAnsiBytes(Guid.NewGuid().ToString("N"), 128),
                    nAllowTime = allowTimeSeconds,
                    nFaceCount = enable ? 1 : 0,
                    nFingferCount = 0,
                    nCardCount = 0,
                    nIrisCount = 0,
                    szUserID = ToFixedAnsiBytes(userId.Trim(), 64),
                    nPalmCount = 0,
                    nPalmStyle = 0,
                    nIrisStyle = 0,
                    nOperType = enable ? 1 : 0
                };

                var output = new NET_OUT_CAPTURE_ACCESS_PERSON_COLLECTION_CMD
                {
                    dwSize = (uint)outputSize
                };

                inputPtr = Marshal.AllocHGlobal(inputSize);
                outputPtr = Marshal.AllocHGlobal(outputSize);
                ZeroMemory(inputPtr, inputSize);
                ZeroMemory(outputPtr, outputSize);
                Marshal.StructureToPtr(input, inputPtr, false);
                Marshal.StructureToPtr(output, outputPtr, false);

                Console.WriteLine(
                    "[DahuaSDK] Direct access-person face collection request: "
                    + $"loginHandle={_loginHandle} "
                    + $"userId={userId.Trim()} "
                    + $"enable={enable} "
                    + "collectionType=1(face) "
                    + $"inputSize={inputSize} "
                    + $"outputSize={outputSize} "
                    + $"allowTimeSeconds={allowTimeSeconds} "
                    + $"inputBytes={Convert.ToHexString(ReadBytes(inputPtr, inputSize))} "
                    + $"timeout={SDK_TIMEOUT}");

                bool result = CLIENT_CaptureAccessPersonCollectionCmd(
                    _loginHandle,
                    inputPtr,
                    outputPtr,
                    SDK_TIMEOUT);

                uint sdkError = SafeGetLastError();

                Console.WriteLine(
                    "[DahuaSDK] Direct access-person face collection result: "
                    + $"result={result} "
                    + $"loginHandle={_loginHandle} "
                    + $"userId={userId.Trim()} "
                    + $"sdkError={sdkError} "
                    + $"hexError=0x{sdkError:X8}");

                return new CaptureAccessResult { Success = result, SdkError = sdkError };
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[DahuaSDK] CaptureAccessPersonFaceCollection exception: "
                    + ex.Message);
                return new CaptureAccessResult { Success = false, SdkError = SafeGetLastError() };
            }
            finally
            {
                FreeHGlobal(ref inputPtr);
                FreeHGlobal(ref outputPtr);
            }
        }

        private const uint NET_EM_CFG_COLLECT_USER_INFO_CFG = 4039u;

        /// <summary>
        /// Enable or disable the device's self-service face collection mode
        /// (NET_EM_CFG_COLLECT_USER_INFO_CFG = 4039).
        ///
        /// This is a device-wide switch. When enabled the device actively looks
        /// for a person and captures their face. The DH_ALARM_FACEINFO_COLLECT
        /// (0x3240) callback fires with szUserID populated from whatever identity
        /// the person entered on the device terminal — NOT from a server-side
        /// per-user command (no such SDK function exists).
        ///
        /// Returns true if the config was written successfully.
        /// </summary>
        public bool EnableDeviceFaceCollectionMode(bool enable)
        {
            ThrowIfDisposed();

            if (!IsLoggedIn)
            {
                Console.WriteLine("[DahuaSDK] EnableDeviceFaceCollectionMode: device not logged in.");
                return false;
            }

            int cfgSize = Marshal.SizeOf<NET_CFG_COLLECT_USER_INFO_CFG_INFO>();
            IntPtr cfgPtr = IntPtr.Zero;
            IntPtr currentCfgPtr = IntPtr.Zero;

            try
            {
                var cfg = new NET_CFG_COLLECT_USER_INFO_CFG_INFO
                {
                    dwSize  = (uint)cfgSize,
                    bEnable = enable ? 1 : 0
                };

                cfgPtr = Marshal.AllocHGlobal(cfgSize);
                ZeroMemory(cfgPtr, cfgSize);
                Marshal.StructureToPtr(cfg, cfgPtr, false);

                byte[] inputBytes = new byte[cfgSize];
                Marshal.Copy(cfgPtr, inputBytes, 0, cfgSize);

                Console.WriteLine(
                    "[DahuaSDK] Face collection config request: "
                    + $"configId={NET_EM_CFG_COLLECT_USER_INFO_CFG} "
                    + $"loginHandle={_loginHandle} "
                    + "channel=-1 "
                    + $"inputSize={cfgSize} "
                    + $"dwSize={cfg.dwSize} "
                    + $"bEnable={cfg.bEnable} "
                    + $"bytes={Convert.ToHexString(inputBytes)} "
                    + $"timeout={SDK_TIMEOUT}");

                currentCfgPtr = Marshal.AllocHGlobal(cfgSize);
                ZeroMemory(currentCfgPtr, cfgSize);

                uint bytesReturned = 0;
                bool getResult = CLIENT_GetDevConfig(
                    _loginHandle,
                    NET_EM_CFG_COLLECT_USER_INFO_CFG,
                    -1,
                    currentCfgPtr,
                    (uint)cfgSize,
                    ref bytesReturned,
                    SDK_TIMEOUT);

                uint getError = SafeGetLastError();
                string getErrorDescription = getError switch
                {
                    0x80000007 => "NET_ILLEGAL_PARAM (user parameter is illegal)",
                    0x80000017 => "NET_NOT_SUPPORTED (SDK function is not supported)",
                    0x80000001 => "NET_ERROR (unknown error)",
                    0 => "no error reported",
                    _ => "unknown SDK error"
                };
                byte[] currentBytes = new byte[cfgSize];
                Marshal.Copy(currentCfgPtr, currentBytes, 0, cfgSize);

                Console.WriteLine(
                    "[DahuaSDK] Face collection config read: "
                    + $"result={getResult} "
                    + $"configId={NET_EM_CFG_COLLECT_USER_INFO_CFG} "
                    + $"loginHandle={_loginHandle} "
                    + "channel=-1 "
                    + $"outputSize={cfgSize} "
                    + $"bytesReturned={bytesReturned} "
                    + $"sdkError={getError} ({getErrorDescription}) "
                    + $"bytes={Convert.ToHexString(currentBytes)}");

                bool result = CLIENT_SetDevConfig(
                    _loginHandle,
                    NET_EM_CFG_COLLECT_USER_INFO_CFG,
                    -1,      // device-level, no channel
                    cfgPtr,
                    (uint)cfgSize,
                    SDK_TIMEOUT);

                uint sdkError = SafeGetLastError();
                string sdkErrorDescription = sdkError switch
                {
                    0x80000007 => "NET_ILLEGAL_PARAM (user parameter is illegal)",
                    0x80000017 => "NET_NOT_SUPPORTED (SDK function is not supported)",
                    0x80000001 => "NET_ERROR (unknown error)",
                    0 => "no error reported",
                    _ => "unknown SDK error"
                };

                Console.WriteLine(
                    "[DahuaSDK] Face collection config write: "
                    + $"result={result} "
                    + $"configId={NET_EM_CFG_COLLECT_USER_INFO_CFG} "
                    + $"loginHandle={_loginHandle} "
                    + "channel=-1 "
                    + $"inputSize={cfgSize} "
                    + $"timeout={SDK_TIMEOUT} "
                    + $"sdkError={sdkError} "
                    + $"hexError=0x{sdkError:X8} "
                    + $"errorDescription={sdkErrorDescription}");

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DahuaSDK] EnableDeviceFaceCollectionMode exception: " + ex.Message);
                return false;
            }
            finally
            {
                FreeHGlobal(ref cfgPtr);
                FreeHGlobal(ref currentCfgPtr);
            }
        }
    }

    // =================================================================
    // NET_CFG_COLLECT_USER_INFO_CFG_INFO
    //
    // Header: dhnetsdk.h — tagNET_CFG_COLLECT_USER_INFO_CFG_INFO
    // Config type: NET_EM_CFG_COLLECT_USER_INFO_CFG = 4039
    // ChannelID must be -1 (device-level, not per-channel).
    //
    // bEnable: "Whether the access control equipment enables the
    //           acquisition mode, and whether it enters the mode of
    //           acquisition while adding"
    //
    // Layout: DWORD dwSize (4) + BOOL bEnable (4) = 8 bytes.
    // =================================================================
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_CFG_COLLECT_USER_INFO_CFG_INFO
    {
        public uint dwSize;

        // Native BOOL = 4-byte int. 0=disabled, non-zero=enabled.
        public int bEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_IN_CAPTURE_ACCESS_PERSON_COLLECTION_CMD
    {
        public uint dwSize;
        public uint nType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] szUUID;

        public int nAllowTime;
        public int nFaceCount;
        public int nFingferCount;
        public int nCardCount;
        public int nIrisCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] szUserID;

        public int nPalmCount;
        public int nPalmStyle;
        public int nIrisStyle;
        public int nOperType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_OUT_CAPTURE_ACCESS_PERSON_COLLECTION_CMD
    {
        public uint dwSize;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    public struct NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY
    {
        public uint dwSize;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 64)]
        public string szIP;

        public int nPort;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 64)]
        public string szUserName;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 64)]
        public string szPassword;

        public int emSpecCap;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 4)]
        public byte[] byReserved;

        public IntPtr pCapParam;

        public int emTLSCap;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 64)]
        public string szLocalIP;

        public int nClientType;
    }

    // IMPORTANT:
    // Do NOT use Pack=1 here.
    // Native SDK structure has natural alignment.
    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_DEVICEINFO_Ex
    {
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 48)]
        public byte[] sSerialNumber;

        public int nAlarmInPortNum;
        public int nAlarmOutPortNum;
        public int nDiskNum;
        public int nDVRType;
        public int nChanNum;

        public byte byLimitLoginTime;
        public byte byLeftLogTimes;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 2)]
        public byte[] bReserved;

        public int nLockLeftTime;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 4)]
        public byte[] Reserved;

        public int nNTlsPort;
        public int nKeyFrameEncrypt;
        public int emAlgorithm;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 8)]
        public byte[] Reserved2;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY
    {
        public uint dwSize;

        public NET_DEVICEINFO_Ex stuDeviceInfo;

        public int nError;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 132)]
        public byte[] byReserved;
    }

    // =================================================================
    // LEGACY ATTENDANCE
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_IN_ATTENDANCE_FINDUSER
    {
        public uint dwSize;

        public int nOffset;

        public int nPagedQueryCount;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_ATTENDANCE_FINDUSER
    {
        public uint dwSize;

        public int nTotalUser;

        public int nMaxUserCount;

        public IntPtr stuUserInfo;

        public int nRetUserCount;

        public int nMaxPhotoDataLength;

        public int nRetPhoteLength;

        public IntPtr pbyPhotoData;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi,
        Pack = 1)]
    public struct NET_ATTENDANCE_USERINFO
    {
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szUserID;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 36)]
        public byte[] szUserName;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szCardNo;

        public int emAuthority;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szPassword;

        public int nPhotoLength;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szClassNumber;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 16)]
        public byte[] szPhoneNumber;

        public int emCardType;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 204)]
        public byte[] byReserved;
    }

    // =================================================================
    // TIME
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_TIME
    {
        public uint dwYear;
        public uint dwMonth;
        public uint dwDay;
        public uint dwHour;
        public uint dwMinute;
        public uint dwSecond;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_TIME_EX
    {
        public uint dwYear;
        public uint dwMonth;
        public uint dwDay;
        public uint dwHour;
        public uint dwMinute;
        public uint dwSecond;
        public uint dwMillisecond;
        public uint dwUTC;

        public uint dwReserved0;
    }

    // =================================================================
    // ACCESS USER FIND
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    public struct NET_IN_USERINFO_START_FIND
    {
        public uint dwSize;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string szUserID;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 64)]
        public string szPassword;

        // Native BOOL = 4 bytes.
        public int bQueryPWDNotEmpty;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_USERINFO_START_FIND
    {
        public uint dwSize;

        public int nTotalCount;

        public int nCapNum;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_IN_USERINFO_DO_FIND
    {
        public uint dwSize;

        public int nStartNo;

        public int nCount;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_USERINFO_DO_FIND
    {
        public uint dwSize;

        public int nRetNum;

        public IntPtr pstuInfo;

        public int nMaxNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 4)]
        public byte[] byReserved;
    }

    // =================================================================
    // ACCESS USER INFO
    //
    // IMPORTANT:
    //
    // Native SDK does NOT use Pack=1 here.
    //
    // Native:
    //
    // int nDoors[32]
    // int nTimeSectionNo[32]
    // int nSpecialDaysSchedule[128]
    // int nFirstEnterDoors[32]
    //
    // therefore managed fields MUST be int[].
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    public struct NET_ACCESS_USER_INFO
    {
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szUserID;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szName;

        public int emUserType;

        public uint nUserStatus;

        public int nUserTime;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szCitizenIDNo;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szPsw;

        public int nDoorNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public int[] nDoors;

        public int nTimeSectionNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public int[] nTimeSectionNo;

        public int nSpecialDaysScheduleNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 128)]
        public int[] nSpecialDaysSchedule;

        public NET_TIME stuValidBeginTime;

        public NET_TIME stuValidEndTime;

        // Native BOOL
        public int bFirstEnter;

        public int nFirstEnterDoorsNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public int[] nFirstEnterDoors;

        public int emAuthority;

        public int nRepeatEnterRouteTimeout;

        public int nFloorNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64 * 16)]
        public byte[] szFloorNo;

        public int nRoom;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32 * 16)]
        public byte[] szRoomNo;

        // Native BOOL
        public int bFloorNoExValid;

        public int nFloorNumEx;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 256 * 4)]
        public byte[] szFloorNoEx;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 256)]
        public byte[] szClassInfo;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szStudentNo;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 128)]
        public byte[] szCitizenAddress;

        public NET_TIME stuBirthDay;

        public int emSex;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 128)]
        public byte[] szDepartment;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szSiteCode;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szPhoneNumber;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 8)]
        public byte[] szDefaultFloor;

        // Native BOOL
        public int bFloorNoEx2Valid;

        public IntPtr pstuFloorsEx2;

        // Native BOOL
        public int bHealthStatus;

        public int nUserTimeSectionsNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 6 * 20)]
        public byte[] szUserTimeSections;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szECType;

        public int emTypeOfCertificate;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 8)]
        public byte[] szCountryOrAreaCode;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szCountryOrAreaName;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szCertificateVersionNumber;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szApplicationAgencyCode;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szIssuingAuthority;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szStartTimeOfCertificateValidity;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 64)]
        public byte[] szEndTimeOfCertificateValidity;

        public int nSignNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 108)]
        public byte[] szActualResidentialAddr;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 256)]
        public byte[] szWorkClass;

        public NET_TIME stuStartTimeInPeriodOfValidity;

        public int emTestItems;

        // Native BOOL
        public int bUseNameEx;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 128)]
        public byte[] szNameEx;

        // Native BOOL
        public int bUserInfoExValid;

        public IntPtr pstuUserInfoEx;

        public uint nAuthOverdueTime;

        public int emGreenCNHealthStatus;

        public int emAllowPermitFlag;

        public int nHolidayGroupIndex;

        public NET_TIME stuUpdateTime;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 8 * 24)]
        public byte[] szValidFroms;

        public int nValidFromsNum;

        public int nValidTosNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 8 * 24)]
        public byte[] szValidTos;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 128)]
        public byte[] szUserIDEx;

        // Native BOOL
        public int bUserIDEx;

        public int nFinancialUserType;

        public int nCustomUserType;

        public uint nCustomUserTypeValue;

        public NET_TIME_EX stuAllowCheckInTime;

        public NET_TIME_EX stuAllowCheckOutTime;

        public IntPtr pstuUserInfoEx2;

        // Native BOOL
        public int bUserInfoEx2Valid;

        public uint nRoleID;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 24)]
        public byte[] szWorkPermitDate;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 24)]
        public byte[] szInductionExpireDate;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 24)]
        public byte[] szMedicalExpireDate;

        public IntPtr pstuUserInfoEx3;

        // Native BOOL
        public int bUserInfoEx3Valid;

        // x64:
        // 580 - (3 * 8) = 556
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 556)]
        public byte[] byReserved;
    }

    // =================================================================
    // ACCESS USER INSERT
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_IN_ACCESS_USER_SERVICE_INSERT
    {
        public uint dwSize;

        public int nInfoNum;

        public IntPtr pUserInfo;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_ACCESS_USER_SERVICE_INSERT
    {
        public uint dwSize;

        public int nMaxRetNum;

        public IntPtr pFailCode;
    }

    // =================================================================
    // ACCESS USER REMOVE (managed structs)
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    public struct NET_IN_ACCESS_USER_SERVICE_REMOVE
    {
        public uint dwSize;

        public int nUserNum;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100 * 32)]
        public byte[] szUserID; // flattened 100 x 32

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100 * 128)]
        public byte[] szUserIDEx; // flattened 100 x 128

        // Native BOOL
        public int bUserIDEx;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_ACCESS_USER_SERVICE_REMOVE
    {
        public uint dwSize;

        public int nMaxRetNum;

        public IntPtr pFailCode;
    }

    // =================================================================
    // FACE INFO
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    public struct NET_ACCESS_FACE_INFO
    {
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 32)]
        public byte[] szUserID;

        public int nFaceData;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 20 * 2048)]
        public byte[] szFaceData;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 20)]
        public int[] nFaceDataLen;

        public int nFacePhoto;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 5)]
        public int[] nInFacePhotoLen;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 5)]
        public int[] nOutFacePhotoLen;

        // Native:
        // char* pFacePhoto[5]
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 5)]
        public IntPtr[] pFacePhoto;

        // Native BOOL
        public int bFaceDataExEnable;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 20)]
        public int[] nMaxFaceDataLen;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 20)]
        public int[] nRetFaceDataLen;

        // Native:
        // char* pFaceDataEx[20]
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 20)]
        public IntPtr[] pFaceDataEx;

        public NET_TIME stuUpdateTime;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 128)]
        public byte[] szUserIDEx;

        // Native BOOL
        public int bUserIDEx;

        public int nEigenData;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 5)]
        public int[] nInEigenDataLen;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 5)]
        public int[] nOutEigenDataLen;

        // Native:
        // char* pEigenData[5]
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 5)]
        public IntPtr[] pEigenData;

        // x64:
        // 1600 - 5*8 = 1560
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 1560)]
        public byte[] byReserved;
    }

    // =================================================================
    // FACE INSERT
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_IN_ACCESS_FACE_SERVICE_INSERT
    {
        public uint dwSize;

        public int nFaceInfoNum;

        public IntPtr pFaceInfo;
    }

    // =================================================================
    // ERROR DETAIL
    //
    // Native:
    //
    // char szExtraInfo[20][256];
    // UINT nRetExtraInfoNum;
    // char szReserved[1020];
    //
    // total = 6144 bytes.
    // =================================================================

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    public struct NET_ERROR_DETAIL
    {
        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 20 * 256)]
        public byte[] szExtraInfo;

        public uint nRetExtraInfoNum;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = 1020)]
        public byte[] szReserved;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    public struct NET_OUT_ACCESS_FACE_SERVICE_INSERT
    {
        public uint dwSize;

        public int nMaxRetNum;

        public IntPtr pFailCode;

        public NET_ERROR_DETAIL stuDetail;
    }

    // =================================================================
    // RESULT MODELS
    // =================================================================

    public class FaceEnrollResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int FailCode { get; set; }

        public uint SdkError { get; set; }
    }

    public class CreateUserResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int FailCode { get; set; }

        public uint SdkError { get; set; }
    }

    public class DeleteUserResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int FailCode { get; set; }

        public uint SdkError { get; set; }
    }

    public class AttendanceUsersResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int TotalUsers { get; set; }

        public int ReturnedUsers { get; set; }

        public List<AttendanceUser> Users { get; set; } =
            new List<AttendanceUser>();
    }

    public class AttendanceUser
    {
        public string UserId { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        public string CardNo { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string ClassNumber { get; set; } = string.Empty;

        public string PhoneNumber { get; set; } = string.Empty;

        public int PhotoLength { get; set; }
    }
}