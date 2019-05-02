// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as BuildXLSdk from "Sdk.BuildXL";
import { NetFx } from "Sdk.BuildXL";

export {BuildXLSdk, NetFx};
export declare const qualifier: BuildXLSdk.DefaultQualifier;

namespace DetoursCrossBitTests
{
    @@public
    export const x64 = Processes.TestPrograms.DetoursCrossBitTests.withQualifier({
        configuration: qualifier.configuration,
        platform: "x64",
        targetFramework: qualifier.targetFramework,
        targetRuntime: "win-x64"
    }).exe;

    @@public
    export const x86 = Processes.TestPrograms.DetoursCrossBitTests.withQualifier({
        configuration: qualifier.configuration,
        platform: "x86",
        targetFramework: qualifier.targetFramework,
        targetRuntime: "win-x64"
    }).exe;
}
