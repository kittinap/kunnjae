// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Transformer {
    /** Seals specified root folder with a set of files; the created pip is tagged with 'tags'. */
    @@public
    export function sealDirectory(rootOrArgs: (Directory | SealDirectoryArguments), files?: File[], tags?: string[], description?: string, scrub?: boolean): FullStaticContentDirectory {
        return _PreludeAmbientHack_Transformer.sealDirectory(rootOrArgs, files, tags, description, scrub);
    }

    /** Seals specified root folder without the need to specify all files provided root is under a readonly mount; the created pip is tagged with 'tags'. */
    @@public
    export function sealSourceDirectory(rootOrArgs: (Directory | SealSourceDirectoryArguments), option?: SealSourceDirectoryOption, tags?: string[], description?: string, patterns?: string[]): SourceDirectory {
        return _PreludeAmbientHack_Transformer.sealSourceDirectory(rootOrArgs, option, tags, description, patterns);
    }

    /** Seals a partial view of specified root folder with a set of files; the created pip is tagged with 'tags'. */
    @@public
    export function sealPartialDirectory(rootOrArgs: (Directory | SealPartialDirectoryArguments), files?: File[], tags?: string[], description?: string): PartialStaticContentDirectory {
        return _PreludeAmbientHack_Transformer.sealPartialDirectory(rootOrArgs, files, tags, description);
    }

    /** Creates a shared opaque directory whose content is the aggregation of a collection of shared opaque directories.
     * The root can be any arbitrary directory that is a common ancestor to all the provided directories.
     * The resulting directory behaves as any other shared opaque, and can be used as a directory dependency.
    */
    @@public
    export function composeSharedOpaqueDirectories(root: (Directory | ComposeSharedOpaqueDirectoriesArguments), directories?: SharedOpaqueDirectory[]): SharedOpaqueDirectory {
        return _PreludeAmbientHack_Transformer.composeSharedOpaqueDirectories(root, directories);
    }

    /** Options for sealing source directory. */
    @@public
    export const enum SealSourceDirectoryOption {
        /** Indicates the SealedSourceDirectory can access only files in the root folder, not recursively. */
        topDirectoryOnly = 0,
        /** Indicates the SealedSourceDirectory can access all files in all directories under the root folder, recursively. */
        allDirectories,
    }

    /** allows a partial sealed directory to be narrowed to a smaller sealed directory */
    @@public
    export function reSealPartialDirectory(dir: StaticDirectory, relativePath: RelativePath, osRestriction?: "win" | "macOS" | "unix") : PartialStaticContentDirectory {
        if ((osRestriction || Context.getCurrentHost().os) !== Context.getCurrentHost().os) return undefined;

        const subFolder = d`${dir.root}/${relativePath}`;
        return sealPartialDirectory(
            subFolder,
            dir.contents.filter(f => f.isWithin(subFolder))
        );
    }

    @@public
    export interface SealDirectoryArguments extends CommonSealDirectoryArguments {
        /** The files to seal in this directory */
        files: File[],

        /** Whether the directory should be scrubbed of files present on disk that are not part of the sealed directory */
        scrub?: boolean;
    }

    @@public
    export interface SealSourceDirectoryArguments extends CommonSealDirectoryArguments {
        /** Optional file pattern to match in this sealed source directory. */
        patterns?: string[],

        /** Optional options for which files to seal. The default is 'topDirectoryOnly' */
        include?: "topDirectoryOnly" | "allDirectories",
    }

    @@public
    export interface SealPartialDirectoryArguments extends CommonSealDirectoryArguments {
        /** The files to seal in this directory */
        files: File[],
    }

    @@public
    export interface CommonSealDirectoryArguments {
        /** The root of the directory to seal. */
        root: Directory,

        /** Optional set of tags to set on this seal directory pip. */
        tags?: string[],

        /** Optional custom description for this seal directory pip. */
        description?: string,
    }

    @@public
    export interface ComposeSharedOpaqueDirectoriesArguments {
        /** The root of the directory to compose in a new sealed directory. */
        root: Directory,

        /** The directories to compose */
        directories: SharedOpaqueDirectory[],
    }
}
