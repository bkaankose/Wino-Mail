using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Platform.MacOS.Services;

/// <summary>Uses Apple's documented quarantine dictionary rather than constructing raw xattr flags.</summary>
internal static class MacQuarantinePolicy
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    public static AttachmentFileOperationResult Ensure(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: "macOS quarantine policy is unavailable.");

        nint cf = 0, services = 0, url = 0, dictionary = 0, agentName = 0;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return new(AttachmentFileOperationStatus.Failed, path, ErrorMessage: "The attachment does not exist.");

            cf = NativeLibrary.Load(CoreFoundation);
            services = NativeLibrary.Load(CoreServices);
            var quarantineKey = Constant(cf, "kCFURLQuarantinePropertiesKey");
            var bytes = Encoding.UTF8.GetBytes(fullPath);
            url = CFURLCreateFromFileSystemRepresentation(0, bytes, bytes.Length, false);
            if (url == 0)
                throw new InvalidOperationException("Could not create the attachment's native file URL.");

            // Preserve existing quarantine information, including its original agent and timestamp.
            if (HasQuarantineProperties(url, quarantineKey))
                return new(AttachmentFileOperationStatus.Succeeded, path);

            agentName = CFStringCreateWithCString(0, "Wino Mail", 0x08000100);
            if (agentName == 0)
                throw new InvalidOperationException("Could not allocate the quarantine agent name.");
            var keys = new[] { Constant(services, "kLSQuarantineTypeKey"), Constant(services, "kLSQuarantineAgentNameKey") };
            var values = new[] { Constant(services, "kLSQuarantineTypeEmailAttachment"), agentName };
            dictionary = CFDictionaryCreate(0, keys, values, keys.Length,
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            if (dictionary == 0)
                throw new InvalidOperationException("Could not allocate quarantine properties.");

            var applied = CFURLSetResourcePropertyForKey(url, quarantineKey, dictionary, out var error);
            try
            {
                if (!applied)
                    return new(AttachmentFileOperationStatus.PolicyBlocked, path, ErrorMessage: "macOS could not apply attachment quarantine.");
            }
            finally { if (error != 0) CFRelease(error); }

            // Writes to unsupported volumes may be ignored without an error. Read from a fresh URL to verify.
            CFRelease(url);
            url = 0;
            url = CFURLCreateFromFileSystemRepresentation(0, bytes, bytes.Length, false);
            return url != 0 && HasQuarantineProperties(url, quarantineKey)
                ? new(AttachmentFileOperationStatus.Succeeded, path)
                : new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: "The destination does not expose attachment quarantine metadata.");
        }
        catch (DllNotFoundException ex) { return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: ex.Message); }
        catch (EntryPointNotFoundException ex) { return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: ex.Message); }
        catch (Exception ex) { return new(AttachmentFileOperationStatus.Failed, path, ErrorMessage: ex.Message); }
        finally
        {
            if (dictionary != 0) CFRelease(dictionary);
            if (agentName != 0) CFRelease(agentName);
            if (url != 0) CFRelease(url);
            if (services != 0) NativeLibrary.Free(services);
            if (cf != 0) NativeLibrary.Free(cf);
        }
    }

    private static bool HasQuarantineProperties(nint url, nint key)
    {
        var read = CFURLCopyResourcePropertyForKey(url, key, out var value, out var error);
        try
        {
            if (!read)
                throw new IOException("macOS could not read attachment quarantine metadata.");
            return value != 0 && CFGetTypeID(value) == CFDictionaryGetTypeID() && CFDictionaryGetCount(value) > 0;
        }
        finally
        {
            if (value != 0) CFRelease(value);
            if (error != 0) CFRelease(error);
        }
    }

    private static nint Constant(nint library, string name)
    {
        var value = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
        return value != 0 ? value : throw new EntryPointNotFoundException(name);
    }

    [DllImport(CoreFoundation)]
    private static extern nint CFURLCreateFromFileSystemRepresentation(nint allocator, [In] byte[] buffer, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreate(nint allocator, [In] nint[] keys, [In] nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFURLSetResourcePropertyForKey(nint url, nint key, nint value, out nint error);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFURLCopyResourcePropertyForKey(nint url, nint key, out nint value, out nint error);
    [DllImport(CoreFoundation)]
    private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)]
    private static extern nuint CFDictionaryGetTypeID();
    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetCount(nint dictionary);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}
