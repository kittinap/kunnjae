// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

module({
    name: "Sdk.Managed.Shared",
    nameResolutionSemantics: NameResolutionSemantics.implicitProjectReferences,
    projects: glob(d`.`, "*.dsc"),
});
