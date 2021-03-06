// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityEditor.PackageManager.UI
{
    internal class PackageAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var allUpdatedAssets = importedAssets.Concat(deletedAssets).Concat(movedAssets).Concat(movedFromAssetPaths);
            var packageJsonsUpdated = false;

            foreach (var asset in allUpdatedAssets)
            {
                var pathComponents = asset.Split('/');
                if (pathComponents[0] != "Packages")
                    continue;
                if (!packageJsonsUpdated && pathComponents.Length == 3 && pathComponents[2] == "package.json")
                {
                    packageJsonsUpdated = true;
                    break;
                }
            }

            if (packageJsonsUpdated)
                PackageDatabase.instance.Refresh(RefreshOptions.OfflineMode | RefreshOptions.ListInstalled);
        }
    }
}
