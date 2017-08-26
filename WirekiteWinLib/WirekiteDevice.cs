﻿/*
 * Wirekite for Windows 
 * Copyright (c) 2017 Manuel Bleichenbacher
 * Licensed under MIT License
 * https://opensource.org/licenses/MIT
 */

using Codecrete.Wirekite.Device.Messages;
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using static Codecrete.Wirekite.Device.NativeMethods;


namespace Codecrete.Wirekite.Device
{
    internal enum DeviceState
    {
        Initializing,
        Ready,
        Closed
    }


    /// <summary>
    /// Wirekite device
    /// </summary>
    /// <remarks>
    /// Main class for interacting with the Wirekite device.
    /// Instances of this class cannot be created directly. Instead, the Wirekit service creates
    /// them when they are plugged in or when the service instance is created.</remarks>
    /// <seealso cref="WirekiteService">The Wirekite service listening to devices being connected or disconnected</seealso>
    public partial class WirekiteDevice : IDisposable
    {
        private const Byte RxEndpointAddress = 0x81; // endpoint 1 / IN
        private const Byte TxEndpointAddress = 0x02; // endpoint 2 / OUT

        private WirekiteService _service;
        internal String DevicePath { get; private set; }
        private SafeFileHandle _deviceHandle;
        private IntPtr _interfaceHandle;
        private DeviceState _deviceState = DeviceState.Initializing;
        private byte[] _rxBuffer;

        private PendingResponseList _pendingResponses = new PendingResponseList();
        private PortList _ports = new PortList();


        internal WirekiteDevice(WirekiteService service, String devicePath, SafeFileHandle deviceHandle, IntPtr interfaceHandle)
        {
            _service = service;
            DevicePath = devicePath;
            _deviceHandle = deviceHandle;
            _interfaceHandle = interfaceHandle;
            ThreadPool.BindHandle(_deviceHandle);

            SubmitReadRequest();
            ResetConfiguration();
            _deviceState = DeviceState.Ready;
        }


        /// <summary>
        /// Closes the Wirekite devices.
        /// </summary>
        /// <remarks>
        /// A closed device can no longer be used. It must be disconnected and connected again,
        /// or the Wirekite service must be restarted.
        /// </remarks>
        public void Close()
        {
            if (_deviceState == DeviceState.Closed)
                return;

            _ports.Clear();
            _pendingResponses.Clear();
            _service.RemoveDevice(this);
            WinUsb_Free(_interfaceHandle);
            _interfaceHandle = IntPtr.Zero;
            _deviceHandle.Dispose();
            _deviceHandle = null;
            _deviceState = DeviceState.Closed;
        }


        private unsafe void SubmitReadRequest()
        {
            _rxBuffer = new byte[64];
            Overlapped overlapped = new Overlapped();
            NativeOverlapped* nativeOverlapped = overlapped.Pack(ReadComplete, _rxBuffer);
            bool success;
            UInt32 numBytes;
            int errorCode;
            fixed (byte* buf = _rxBuffer)
            {
                success = WinUsb_ReadPipe(_interfaceHandle, RxEndpointAddress, buf, (UInt32)_rxBuffer.Length, out numBytes, nativeOverlapped);
                errorCode = Marshal.GetLastWin32Error();
            }

            if (success)
            {
                // call was executed synchronously
                ReadComplete(0, numBytes, nativeOverlapped);
            }
            else
            {
                if (errorCode != ERROR_IO_PENDING)
                {
                    Overlapped.Unpack(nativeOverlapped);
                    Overlapped.Free(nativeOverlapped);
                    WirekiteException.ThrowWin32Exception("Failed to submit read request for Wirekite device");
                }
            }
        }


        private unsafe void ReadComplete(UInt32 errorCode, UInt32 numBytes, NativeOverlapped* nativeOverlapped)
        {
            byte[] buffer = _rxBuffer;
            _rxBuffer = null;

            Overlapped.Unpack(nativeOverlapped);
            Overlapped.Free(nativeOverlapped);

            if (errorCode == 2 || errorCode == 31)
                return; // device was probably removed; broadcast will arrive later

            if (errorCode != 0)
                throw new WirekiteException("Error on reading data from device", new Win32Exception((int)errorCode));

            if (_deviceState != DeviceState.Closed)
                SubmitReadRequest();

            HandleInput(buffer, (int)numBytes);
        }


        private void HandleInput(byte[] buffer, int numBytes)
        {
            byte messageType = buffer[2];
            if (messageType == Message.MessageTypeConfigResponse)
            {
                ConfigResponse response = new ConfigResponse();
                response.Read(buffer, 0);
                if (_deviceState == DeviceState.Ready || response.RequestId == 0xffff)
                    HandleConfigResponse(response);
            }
            else if (messageType == Message.MessageTypePortEvent)
            {
                PortEvent evt = new PortEvent();
                evt.Read(buffer, 0);
                if (_deviceState == DeviceState.Ready)
                    HandlePortEvent(evt);
            }
            else
            {
                throw new WirekiteException(String.Format("Invalid message type ({0}) received", messageType));
            }
        }


        private void HandleConfigResponse(ConfigResponse response)
        {

            _pendingResponses.PutResponse(response.RequestId, response);
        }


        private void HandlePortEvent(PortEvent evt)
        {
            Port port = _ports.GetPort(evt.PortId);
            if (port == null)
                throw new WirekiteException(String.Format("Port event received for invalid port ID {0}", evt.PortId));

            PortType type = port.Type;
            switch (type)
            {
                case PortType.DigitalInputOnDemand:
                case PortType.DigitalInputPrecached:
                case PortType.DigitalInputTriggering:
                case PortType.DigitalOutput:
                    HandleDigitalPinEvent(evt);
                    break;

                case PortType.AnalogInputOnDemand:
                case PortType.AnalogInputSampling:
                    HandleAnalogPinEvent(evt);
                    break;

                default:
                    throw new WirekiteException(String.Format("Port event received for invalid for type {0} of port ID {1}", type, evt.PortId));
            }
        }


        /// <summary>
        /// Resets the device to is initial configuration.
        /// </summary>
        /// <remarks>
        /// After the reset, all configured ports are invalid and all pins and
        /// modules are available for use again.
        /// </remarks>
        public void ResetConfiguration()
        {
            ConfigRequest request = new ConfigRequest
            {
                Action = Message.ConfigActionReset,
                RequestId = 0xffff
            };

            SendConfigRequest(request);

            _pendingResponses.Clear();
            _ports.Clear();
        }

        private ConfigResponse SendConfigRequest(ConfigRequest request)
        {
            if (request.RequestId == 0)
                request.RequestId = _ports.NextRequestId();
            WriteMessage(request);
            ConfigResponse response = _pendingResponses.WaitForResponse(request.RequestId) as ConfigResponse;
            if (response.Result != 0)
                throw new WirekiteException(String.Format("Configuration failed with code {0}", response.Result));
            return response;
        }

        private void SendPortRequest(PortRequest request)
        {
            request.RequestId = _ports.NextRequestId();
            WriteMessage(request);
        }


        private void WriteMessage(Message message)
        {
            int messageSize = message.MessageSize;
            byte[] buffer = new byte[messageSize];
            message.Write(buffer, 0);

            unsafe
            {
                Overlapped overlapped = new Overlapped();
                NativeOverlapped* nativeOverlapped = overlapped.Pack(WriteComplete, buffer);
                bool success;
                UInt32 numBytes;
                int errorCode;
                fixed (byte* buf = buffer)
                {
                    success = WinUsb_WritePipe(_interfaceHandle, TxEndpointAddress, buf, (UInt32)messageSize, out numBytes, nativeOverlapped);
                    errorCode = Marshal.GetLastWin32Error();
                }

                if (success)
                {
                    // call was executed synchronously
                    WriteComplete(0, numBytes, nativeOverlapped);
                }
                else
                {
                    if (errorCode != ERROR_IO_PENDING)
                    {
                        Overlapped.Unpack(nativeOverlapped);
                        Overlapped.Free(nativeOverlapped);
                        if (errorCode == 22)
                            return; // device was probably removed; broadcast will arrive later

                        WirekiteException.ThrowWin32Exception("Failed to submit write request to Wirekite device");
                    }
                }
            }
        }


        private unsafe void WriteComplete(UInt32 errorCode, UInt32 numBytes, NativeOverlapped* nativeOverlapped)
        {
            Overlapped.Unpack(nativeOverlapped);
            Overlapped.Free(nativeOverlapped);

            if (errorCode == 2 || errorCode == 31)
                return; // device was probably removed; broadcast will arrive later

            if (errorCode != 0)
                throw new WirekiteException("Error on writing data to device", new Win32Exception((int)errorCode));
        }


        /// <summary>
        /// Finalizes this instance.
        /// </summary>
        ~WirekiteDevice()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes of all resources linked to this device.
        /// </summary>
        /// <param name="disposing">Indicates if this method is called from <c>Dispose()</c></param>
        protected virtual void Dispose(bool disposing)
        {
            if (_deviceState == DeviceState.Closed)
                return;

            Close();
        }


        /// <summary>
        /// Disposes of all resources linked to this device.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
