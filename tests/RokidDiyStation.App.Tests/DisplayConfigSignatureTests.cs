// SPDX-License-Identifier: MIT
//
// Tests that lock the display-configuration (CCD) P/INVOKE
// SIGNATURES to the Windows SDK.
//
// THE BUG THIS PREVENTS
// ---------------------
// The CCD binding declared the three functions with signatures that
// do not exist:
//
//   GetDisplayConfigBufferSizes(flags, *path, *mode, *target, *source)
//   QueryDisplayConfig(flags, *path, paths, *mode, modes,
//                      *target, targets, *source, sources)
//   SetDisplayConfig(flags, paths, pathCount, modes, modeCount)
//
// The SDK (10.0.26100.0, um/WinUser.h) says:
//
//   LONG GetDisplayConfigBufferSizes(
//       UINT32 flags, UINT32* numPathArrayElements,
//       UINT32* numModeInfoArrayElements);
//
//   LONG SetDisplayConfig(
//       UINT32 numPathArrayElements, DISPLAYCONFIG_PATH_INFO* pathArray,
//       UINT32 numModeInfoArrayElements, DISPLAYCONFIG_MODE_INFO* modeInfoArray,
//       UINT32 flags);
//
//   LONG QueryDisplayConfig(
//       UINT32 flags, UINT32* numPathArrayElements,
//       DISPLAYCONFIG_PATH_INFO* pathArray, UINT32* numModeInfoArrayElements,
//       DISPLAYCONFIG_MODE_INFO* modeInfoArray,
//       DISPLAYCONFIG_TOPOLOGY_ID* currentTopologyId);
//
// There are NO target-name or source-name arrays in QueryDisplayConfig,
// and SetDisplayConfig takes the FLAGS LAST.
//
// WHY A STRUCT-SIZE TEST WAS NOT ENOUGH
// -------------------------------------
// Every struct was the right size.  The bug was in the ARGUMENT
// LISTS, and a wrong argument list produces no exception at all: the
// extra arguments land in the wrong stack slots, and the API answers
// ERROR_INVALID_PARAMETER (87) for a configuration that is perfectly
// valid.  The symptom looked like a broken machine.
//
// So the argument counts are asserted here.  This is the cheapest
// possible check that the binding still matches the SDK.
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;

using Xunit;

namespace RokidDiyStation.App.Tests;

public class DisplayConfigSignatureTests
{
    private static MethodInfo Native(string name)
        => typeof(DisplayConfigController)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{name} is not declared");

    [Fact]
    public void GetDisplayConfigBufferSizesTakesThreeArguments()
    {
        // flags, *pathCount, *modeCount.
        var method = Native("GetDisplayConfigBufferSizes");

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(uint), parameters[0].ParameterType);

        // An "out" parameter is reported by reflection as a by-ref
        // type (UInt32&), not as uint with IsOut set.
        Assert.True(parameters[1].ParameterType.IsByRef);
        Assert.Equal(typeof(uint), parameters[1].ParameterType.GetElementType());
        Assert.True(parameters[2].ParameterType.IsByRef);
        Assert.Equal(typeof(uint), parameters[2].ParameterType.GetElementType());
    }

    [Fact]
    public void QueryDisplayConfigTakesSixArgumentsAndNoNameArrays()
    {
        // flags, *pathCount, paths, *modeCount, modes, topologyId.
        var method = Native("QueryDisplayConfig");

        var parameters = method.GetParameters();
        Assert.Equal(6, parameters.Length);
        Assert.Equal(typeof(uint), parameters[0].ParameterType);
        Assert.True(parameters[1].ParameterType.IsByRef);
        Assert.True(parameters[3].ParameterType.IsByRef);

        // The last parameter is the topology id, and it is a POINTER -
        // the SDK requires it to be NULL unless QDC_DATABASE_CURRENT is
        // set.  Passing a pointer where NULL is required is what made
        // every other query answer 87.
        Assert.Equal(typeof(IntPtr), parameters[5].ParameterType);

        // There is no target-name or source-name array in this
        // signature.  The names come from DisplayConfigGetDeviceInfo,
        // one target at a time.
        var arrayTypes = parameters
            .Where(p => p.ParameterType.IsArray)
            .Select(p => p.ParameterType.GetElementType())
            .ToList();
        Assert.Equal(2, arrayTypes.Count);
        Assert.All(arrayTypes, t => Assert.True(
            t == typeof(DisplayConfigController.DISPLAYCONFIG_PATH_INFO) ||
            t == typeof(DisplayConfigController.DISPLAYCONFIG_MODE_INFO)));
    }

    [Fact]
    public void SetDisplayConfigTakesFiveArgumentsWithTheFlagsLast()
    {
        // pathCount, paths, modeCount, modes, flags.
        var method = Native("SetDisplayConfig");

        var parameters = method.GetParameters();
        Assert.Equal(5, parameters.Length);

        // The counts come first, then the arrays, and the FLAGS are
        // LAST.  The old binding had the flags first, which put the
        // path count where the API expected the flags.
        Assert.Equal(typeof(uint), parameters[0].ParameterType);
        Assert.Equal(typeof(DisplayConfigController.DISPLAYCONFIG_PATH_INFO[]), parameters[1].ParameterType);
        Assert.Equal(typeof(uint), parameters[2].ParameterType);
        Assert.Equal(typeof(DisplayConfigController.DISPLAYCONFIG_MODE_INFO[]), parameters[3].ParameterType);
        Assert.Equal(typeof(uint), parameters[4].ParameterType);
    }

    [Fact]
    public void EveryNativeImportNamesTheRightEntryPoint()
    {
        // A DllImport without an explicit EntryPoint binds to the
        // method name, which is how a wrong overload gets picked up.
        var imports = typeof(DisplayConfigController)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<DllImportAttribute>())
            .Where(a => a is not null)
            .ToList();

        Assert.NotEmpty(imports);
        Assert.All(imports, a => Assert.False(
            string.IsNullOrWhiteSpace(a!.EntryPoint),
            "every DllImport must name its entry point explicitly"));
    }
}
