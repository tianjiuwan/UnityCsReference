// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor.PackageManager.UI.AssetStore;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Button = UnityEngine.UIElements.Button;

namespace UnityEditor.PackageManager.UI
{
    internal class PackageDetails : VisualElement
    {
        internal new class UxmlFactory : UxmlFactory<PackageDetails> {}

        private IPackage m_Package;
        private IPackageVersion m_Version;
        private IPackage package
        {
            get { return m_Package; }
            set { m_Package = value; }
        }

        private IPackageVersion displayVersion
        {
            get { return m_Version ?? m_Package?.primaryVersion; }
            set { m_Version = value; }
        }

        private IPackageVersion targetVersion
        {
            get
            {
                var isInstalledVersion = displayVersion?.isInstalled ?? false;
                return isInstalledVersion ? package.recommendedVersion : displayVersion;
            }
        }

        private static readonly string k_EmptyDescriptionClass = "empty";

        internal enum PackageAction
        {
            Add,
            Remove,
            Update,
            Enable,
            Disable,
            UpToDate,
            Current,
            Local,
            Git,
            Embedded,
            Download,
            Upgrade,
            Import
        }

        internal static readonly string[] k_PackageActionVerbs = { "Install", "Remove", "Update to", "Enable", "Disable", "Up to date", "Current", "Local", "Git", "Embedded", "Download", "Update", "Import" };
        private static readonly string[] k_PackageActionInProgressVerbs = { "Installing", "Removing", "Updating to", "Enabling", "Disabling", "Up to date", "Current", "Local", "Git", "Embedded", "Cancel", "Cancel", "Import" };
        internal static readonly PackageTag[] k_VisibleTags =
        {
            PackageTag.Verified,
            PackageTag.InDevelopment,
            PackageTag.Local,
            PackageTag.Git,
            PackageTag.Preview,
            PackageTag.AssetStore,
            PackageTag.Deprecated
        };

        private static Texture2D s_LoadingTexture;

        // We limit the number of entries shown so as to not overload the dialog.
        // The relevance of results beyond this limit is also questionable.
        private const int MaxDependentList = 10;

        public PackageDetails()
        {
            var root = Resources.GetTemplate("PackageDetails.uxml");
            Add(root);

            cache = new VisualElementCache(root);

            PackageManagerExtensions.ExtensionCallback(() =>
            {
                foreach (var extension in PackageManagerExtensions.Extensions)
                    customContainer.Add(extension.CreateExtensionUI());
            });

            root.StretchToParentSize();

            SetContentVisibility(false);
            SetUpdateVisibility(false);
            developButton.visible = false;
            removeButton.visible = false;
            importButton.visible = false;
            downloadButton.visible = false;

            developButton.clickable.clicked += DevelopClick;
            detailAuthorLink.clickable.clicked += AuthorClick;
            updateButton.clickable.clicked += UpdateClick;
            removeButton.clickable.clicked += RemoveClick;
            importButton.clickable.clicked += ImportClick;
            downloadButton.clickable.clicked += DownloadOrCancelClick;
            detailDescMore.clickable.clicked += DescMoreClick;
            detailDescLess.clickable.clicked += DescLessClick;

            detailDesc.RegisterCallback<GeometryChangedEvent>(DescriptionGeometryChangeEvent);

            var editManifestIconButton = new IconButton(Resources.GetIconPath("edit"));
            editManifestIconButton.clickable.clicked += EditPackageManifestClick;
            editPackageManifestButton.Add(editManifestIconButton);

            GetTagLabel(PackageTag.Verified).text = ApplicationUtil.instance.shortUnityVersion + " verified";
        }

        private void OnEditorSelectionChanged()
        {
            var manifestAsset = GetDisplayPackageManifestAsset();
            if (manifestAsset == null)
                return;

            var onlyContainsCurrentPackageManifest = Selection.count == 1 && Selection.Contains(manifestAsset);
            editPackageManifestButton.SetEnabled(!onlyContainsCurrentPackageManifest);
        }

        public UnityEngine.Object GetDisplayPackageManifestAsset()
        {
            var assetPath = displayVersion?.packageInfo?.assetPath;
            if (string.IsNullOrEmpty(assetPath))
                return null;
            return AssetDatabase.LoadMainAssetAtPath(Path.Combine(assetPath, "package.json"));
        }

        private void EditPackageManifestClick()
        {
            var manifestAsset = GetDisplayPackageManifestAsset();
            if (manifestAsset == null)
                Debug.LogWarning("Could not find package.json asset for package: " + displayVersion.displayName);
            else
                Selection.activeObject = manifestAsset;
        }

        public void Setup()
        {
            ApplicationUtil.instance.onFinishCompiling += RefreshPackageActionButtons;

            PackageDatabase.instance.onPackagesChanged += OnPackagesChanged;
            PackageDatabase.instance.onPackageVersionUpdated += OnPackageVersionUpdated;

            PackageDatabase.instance.onPackageOperationStart += OnOperationStartOrFinish;
            PackageDatabase.instance.onPackageOperationFinish += OnOperationStartOrFinish;

            PackageDatabase.instance.onDownloadProgress += OnDownloadProgress;

            SelectionManager.instance.onSelectionChanged += OnSelectionChanged;

            PackageFiltering.instance.onFilterTabChanged += filterTab => OnSelectionChanged(SelectionManager.instance.GetSelections());

            PackageManagerPrefs.instance.onShowDependenciesChanged += SetDependenciesVisibility;

            // manually call the callback function once on initialization to refresh the UI
            OnSelectionChanged(SelectionManager.instance.GetSelections());

            Selection.selectionChanged += OnEditorSelectionChanged;
        }

        private void OnDownloadProgress(IPackage package, DownloadProgress progress)
        {
            if (displayVersion?.packageUniqueId == package.uniqueId)
            {
                if (progress.state == DownloadProgress.State.Error || progress.state == DownloadProgress.State.Aborted)
                {
                    downloadProgress.Hide();
                    RefreshErrorDisplay();
                }
                else
                {
                    downloadProgress.SetProgress(progress.total == 0 ? 0 : progress.current / (float)progress.total);
                }
                RefreshImportAndDownloadButtons();
            }
        }

        private void SetContentVisibility(bool visible)
        {
            UIUtils.SetElementDisplay(detailContainer, visible);
            UIUtils.SetElementDisplay(packageToolbarContainer, visible);
            UIUtils.SetElementDisplay(packageToolbarLeftArea, visible);
        }

        internal void OnSelectionChanged(IEnumerable<IPackageVersion> selected)
        {
            var version = selected.FirstOrDefault();
            if (version != null)
                SetPackage(PackageDatabase.instance.GetPackage(version), version);
            else
                SetPackage(null);
        }

        internal void SetDependenciesVisibility(bool value)
        {
            if (value)
                dependencies.SetDependencies(displayVersion.dependencies);
            UIUtils.SetElementDisplay(dependencies, value);
        }

        private void SetUpdateVisibility(bool value)
        {
            UIUtils.SetElementDisplay(updateButton, value);
        }

        void RefreshExtensions(IPackageVersion version)
        {
            var packageInfo = version?.packageInfo;
            PackageManagerExtensions.ExtensionCallback(() =>
            {
                foreach (var extension in PackageManagerExtensions.Extensions)
                    extension.OnPackageSelectionChange(packageInfo);

                foreach (var extension in PackageManagerExtensions.ToolbarExtensions)
                    extension.OnPackageSelectionChange(packageInfo, packageToolbarContainer);
            });
        }

        private void SetDisplayVersion(IPackageVersion version)
        {
            displayVersion = version;
            detailScrollView.scrollOffset = new Vector2(0, 0);

            var detailVisible = package != null && displayVersion != null;
            if (!detailVisible)
            {
                UIUtils.SetElementDisplay(customContainer, false);
                RefreshExtensions(null);
            }
            else
            {
                var isBuiltIn = displayVersion.HasTag(PackageTag.BuiltIn);
                var isAssetStorePackage = displayVersion.HasTag(PackageTag.AssetStore);

                SetUpdateVisibility(true);

                detailTitle.text = displayVersion.displayName;

                UIUtils.SetElementDisplay(detailNameContainer, !isAssetStorePackage);
                detailName.text = displayVersion.name;

                RefreshLinks();

                RefreshDescription();

                RefreshCategories();

                detailVersion.text = $"Version {displayVersion.version.StripTag()}";
                if (isAssetStorePackage && displayVersion.versionString != displayVersion.version.ToString())
                {
                    detailVersion.text = $"Version {displayVersion.versionString}";
                }

                foreach (var tag in k_VisibleTags)
                    UIUtils.SetElementDisplay(GetTagLabel(tag), displayVersion.HasTag(tag));

                UIUtils.SetElementDisplay(editPackageManifestButton, displayVersion.isInstalled && !displayVersion.HasTag(PackageTag.BuiltIn));

                sampleList.SetPackageVersion(displayVersion);

                RefreshAuthor();

                RefreshPublishedDate();

                UIUtils.SetElementDisplay(detailVersion, !isBuiltIn);

                UIUtils.SetElementDisplay(customContainer, true);
                RefreshExtensions(displayVersion);

                SetDependenciesVisibility(PackageManagerPrefs.instance.showPackageDependencies);

                RefreshSupportedUnityVersions();

                RefreshSizeInfo();

                RefreshSupportingImages();

                RefreshPackageActionButtons();
                RefreshImportAndDownloadButtons();
            }

            OnEditorSelectionChanged();

            // Set visibility
            SetContentVisibility(detailVisible);
            RefreshErrorDisplay();
        }

        private void DescriptionGeometryChangeEvent(GeometryChangedEvent evt)
        {
            if (displayVersion == null)
                return;

            var isAssetStorePackage = displayVersion.HasTag(PackageTag.AssetStore);
            var lineHeight = detailDesc.computedStyle.unityFont.value.lineHeight + 2;
            if (isAssetStorePackage && !UIUtils.IsElementVisible(detailDescMore) && !UIUtils.IsElementVisible(detailDescLess) &&
                evt.newRect.height > lineHeight * 4)
            {
                UIUtils.SetElementDisplay(detailDescMore, true);
                UIUtils.SetElementDisplay(detailDescLess, false);
                detailDesc.style.maxHeight = lineHeight * 3;
            }
        }

        private void RefreshDescription()
        {
            var hasDescription = !string.IsNullOrEmpty(displayVersion.description);
            detailDesc.EnableClass(k_EmptyDescriptionClass, !hasDescription);
            detailDesc.style.maxHeight = int.MaxValue;
            detailDesc.text = hasDescription ? displayVersion.description : "There is no description for this package.";
            UIUtils.SetElementDisplay(detailDescMore, false);
            UIUtils.SetElementDisplay(detailDescLess, false);
        }

        private void RefreshAuthor()
        {
            UIUtils.SetElementDisplay(detailAuthorContainer, !string.IsNullOrEmpty(displayVersion.author));
            if (!string.IsNullOrEmpty(displayVersion.author))
            {
                var isAssetStorePackage = displayVersion.HasTag(PackageTag.AssetStore);
                if (isAssetStorePackage)
                {
                    UIUtils.SetElementDisplay(detailAuthorText, false);
                    UIUtils.SetElementDisplay(detailAuthorLink, true);
                    detailAuthorLink.text = displayVersion.author;
                }
                else
                {
                    UIUtils.SetElementDisplay(detailAuthorText, true);
                    UIUtils.SetElementDisplay(detailAuthorLink, false);
                    detailAuthorText.text = displayVersion.author;
                }
            }
        }

        private void RefreshPublishedDate()
        {
            var isAssetStorePackage = displayVersion.HasTag(PackageTag.AssetStore);
            if (isAssetStorePackage)
            {
                detailDate.text = (displayVersion.publishedDate != null) ? $"{displayVersion.publishedDate.Value:MMMM dd,  yyyy}" : string.Empty;
            }
            else
            {
                // If the package details is not enabled, don't update the date yet as we are fetching new information
                if (enabledSelf)
                {
                    detailDate.text = string.Empty;

                    // In Development packages are not published, so we do not show any published date
                    if (displayVersion != null && !displayVersion.HasTag(PackageTag.InDevelopment))
                    {
                        if (displayVersion.publishedDate != null)
                            detailDate.text = $"{displayVersion.publishedDate.Value:MMMM dd,  yyyy}";
                        else if (displayVersion.HasTag(PackageTag.Core) || displayVersion.isInstalled)
                        {
                            // For core packages, or installed packages that are bundled with Unity without being published, use Unity's build date
                            var unityBuildDate = new DateTime(1970, 1, 1, 0, 0, 0, 0);
                            unityBuildDate = unityBuildDate.AddSeconds(InternalEditorUtility.GetUnityVersionDate());
                            detailDate.text = $"{unityBuildDate:MMMM dd, yyyy}";
                        }
                    }
                }
            }

            UIUtils.SetElementDisplay(detailDateContainer, !string.IsNullOrEmpty(detailDate.text));
        }

        private void RefreshCategories()
        {
            var isAssetStorePackage = displayVersion.HasTag(PackageTag.AssetStore);
            UIUtils.SetElementDisplay(detailCategories, isAssetStorePackage && !string.IsNullOrEmpty(displayVersion.category));
            detailCategories.Clear();

            if (isAssetStorePackage)
            {
                var categories = displayVersion.category.Split('/');
                var parentCategory = "/";
                foreach (var category in categories)
                {
                    var lower = category.ToLower(CultureInfo.InvariantCulture);
                    var url = $"{AssetStoreUtils.instance.assetStoreUrl}{parentCategory}{lower}";
                    detailCategories.Add(new Button(() => { ApplicationUtil.instance.OpenURL(url); })
                        {text = category, classList = {"category", "unity-button", "link"}});
                    parentCategory += lower + "/";
                }
            }
        }

        private void RefreshLinks()
        {
            var isBuiltIn = displayVersion.HasTag(PackageTag.BuiltIn);
            var isAssetStorePackage = displayVersion.HasTag(PackageTag.AssetStore);
            detailLinks.Clear();
            if (!isAssetStorePackage)
            {
                UIUtils.SetElementDisplay(detailLinksContainer, true);
                detailLinks.Add(new Button(ViewDocClick) { text = "View documentation", classList = { "unity-button", "link" }});

                if (UpmPackageDocs.HasChangelog(displayVersion))
                    detailLinks.Add(new Button(ViewChangelogClick) { text = "View changelog", classList = { "unity-button", "link" } });

                if (!isBuiltIn)
                    detailLinks.Add(new Button(ViewLicensesClick) { text = "View licenses", classList = { "unity-button", "link" } });
            }
            else
            {
                UIUtils.SetElementDisplay(detailLinksContainer, displayVersion.links.Any());
                foreach (var link in displayVersion.links)
                {
                    var url = $"{AssetStoreUtils.instance.assetStoreUrl}{link.url}";
                    detailLinks.Add(new Button(() => { ApplicationUtil.instance.OpenURL(url); })
                        { text = link.name, classList = { "unity-button", "link" }});
                }
            }
        }

        private void RefreshSupportedUnityVersions()
        {
            UIUtils.SetElementDisplay(detailUnityVersionsContainer, displayVersion.supportedVersion != null);
            detailUnityVersions.text = $"{displayVersion.supportedVersion} or higher";
        }

        private void RefreshSizeInfo()
        {
            UIUtils.SetElementDisplay(detailSizesContainer, displayVersion.sizes.Any());
            detailSizes.Clear();

            var sizeInfo = displayVersion.sizes.FirstOrDefault(info => info.supportedUnityVersion == displayVersion.supportedVersion);
            if (sizeInfo == null)
                sizeInfo = displayVersion.sizes.LastOrDefault();

            if (sizeInfo != null)
                detailSizes.Add(new Label($"Size: {UIUtils.convertToHumanReadableSize(sizeInfo.downloadSize)} (Number of files: {sizeInfo.assetCount})"));
        }

        private void RefreshSupportingImages()
        {
            UIUtils.SetElementDisplay(detailImagesContainer, displayVersion.images.Any());
            detailImages.Clear();

            if (s_LoadingTexture == null)
                s_LoadingTexture = (Texture2D)EditorGUIUtility.LoadRequired("Icons/UnityLogo.png");

            long id;
            if (long.TryParse(displayVersion.uniqueId, out id))
            {
                foreach (var packageImage in displayVersion.images)
                {
                    var image = new Label {classList = {"image"}};

                    if (packageImage.type == PackageImage.ImageType.Youtube || packageImage.type == PackageImage.ImageType.Sketchfab)
                    {
                        image.AddToClassList("url");
                        image.RegisterCallback<MouseDownEvent>(evt => { ApplicationUtil.instance.OpenURL(packageImage.url); });
                    }

                    image.style.backgroundImage = s_LoadingTexture;
                    detailImages.Add(image);

                    AssetStoreDownloadOperation.instance.DownloadImageAsync(id, packageImage.thumbnailUrl, (retId, texture) =>
                    {
                        if (retId.ToString() == displayVersion?.uniqueId)
                            image.style.backgroundImage = texture;
                    });
                }
            }
        }

        public void SetPackage(IPackage package, IPackageVersion version = null)
        {
            version = version ?? package?.primaryVersion;
            if (package == this.package && version == displayVersion)
                return;

            this.package = package;
            ShowVersion(version);
        }

        internal void OnPackagesChanged(IEnumerable<IPackage> added, IEnumerable<IPackage> removed, IEnumerable<IPackage> updated)
        {
            if (package == null)
                return;
            var newPackage = updated.FirstOrDefault(p => p.uniqueId == package.uniqueId);
            if (newPackage != null)
            {
                var newVersion = displayVersion == null ? null : newPackage.versions.FirstOrDefault(v => v.uniqueId == displayVersion.uniqueId);
                SetPackage(newPackage, newVersion);
            }
            var deps = displayVersion?.dependencies;
            if (UIUtils.IsElementVisible(dependencies) && deps != null && deps.Length > 0)
                dependencies.SetDependencies(deps);
        }

        internal void OnPackageVersionUpdated(IPackageVersion version)
        {
            if (displayVersion?.uniqueId != version.uniqueId)
                return;

            SetEnabled(true);
            SetDisplayVersion(version);
        }

        private void ShowVersion(IPackageVersion version)
        {
            SetEnabled(true);

            if (version != null && !version.isFullyFetched)
            {
                SetEnabled(false);
                PackageDatabase.instance.FetchExtraInfo(version);
            }

            SetDisplayVersion(version);
        }

        private void RefreshErrorDisplay()
        {
            var error = displayVersion?.errors?.FirstOrDefault() ?? package?.errors?.FirstOrDefault();
            if (error == null)
                detailError.ClearError();
            else
            {
                detailError.AdjustSize(detailScrollView.verticalScroller.visible);
                detailError.SetError(error);
                detailError.onCloseError = () => PackageDatabase.instance.ClearPackageErrors(package);
            }
        }

        internal void OnOperationStartOrFinish(IPackage package)
        {
            RefreshPackageActionButtons();
        }

        private void RefreshPackageActionButtons()
        {
            RefreshAddButton();
            RefreshRemoveButton();
            RefreshDevelopButton();
        }

        private void RefreshAddButton()
        {
            var installed = package?.installedVersion;
            var targetVersion = this.targetVersion;
            var visibleFlag = !(installed?.isVersionLocked ?? false) && displayVersion != null && !displayVersion.HasTag(PackageTag.AssetStore) && targetVersion != null;
            if (visibleFlag)
            {
                var installInProgress = PackageDatabase.instance.IsInstallInProgress(displayVersion);
                var enableButton = installed != targetVersion && !installInProgress &&
                    !PackageDatabase.instance.isInstallOrUninstallInProgress && !ApplicationUtil.instance.isCompiling;

                SemVersion versionToUpdateTo = null;
                var action = displayVersion.HasTag(PackageTag.BuiltIn) ? PackageAction.Enable : PackageAction.Add;
                if (installed != null)
                {
                    if (installed == targetVersion)
                        action = targetVersion == package.recommendedVersion ? PackageAction.UpToDate : PackageAction.Current;
                    else
                    {
                        action = PackageAction.Update;
                        versionToUpdateTo = targetVersion.version;
                    }
                }
                updateButton.SetEnabled(enableButton);
                updateButton.text = GetButtonText(action, installInProgress, versionToUpdateTo);
            }
            UIUtils.SetElementDisplay(updateButton, visibleFlag);
        }

        private void RefreshRemoveButton()
        {
            var visibleFlag = displayVersion?.canBeRemoved ?? false;
            if (visibleFlag)
            {
                var installed = package?.installedVersion;
                var action = displayVersion.HasTag(PackageTag.BuiltIn) ? PackageAction.Disable : PackageAction.Remove;
                var removeInProgress = PackageDatabase.instance.IsUninstallInProgress(package);

                var enableButton = !ApplicationUtil.instance.isCompiling && !removeInProgress
                    && !PackageDatabase.instance.isInstallOrUninstallInProgress && installed == displayVersion;

                removeButton.SetEnabled(enableButton);
                removeButton.text = GetButtonText(action, removeInProgress);
            }
            UIUtils.SetElementDisplay(removeButton, visibleFlag);
        }

        private void RefreshDevelopButton()
        {
            var visibleFlag = displayVersion?.canBeEmbedded ?? false;
            if (visibleFlag)
            {
                var enableButton = !ApplicationUtil.instance.isCompiling && !PackageDatabase.instance.isInstallOrUninstallInProgress;
                developButton.SetEnabled(enableButton);
            }
            UIUtils.SetElementDisplay(developButton, visibleFlag);
        }

        private void RefreshImportAndDownloadButtons()
        {
            if (displayVersion == null)
                return;

            var isAssetStore = displayVersion.HasTag(PackageTag.AssetStore);
            if (!isAssetStore)
            {
                UIUtils.SetElementDisplay(importButton, false);
                UIUtils.SetElementDisplay(downloadButton, false);
                downloadProgress.Hide();
                return;
            }

            importButton.text = GetButtonText(PackageAction.Import);

            var downloadInProgress = PackageDatabase.instance.IsDownloadInProgress(displayVersion);
            downloadButton.text = GetButtonText(package.state == PackageState.Outdated ? PackageAction.Upgrade : PackageAction.Download, downloadInProgress);

            var enableButton = !ApplicationUtil.instance.isCompiling;

            UIUtils.SetElementDisplay(importButton, true);
            importButton.SetEnabled(enableButton && displayVersion.isAvailableOnDisk);

            UIUtils.SetElementDisplay(downloadButton, true);
            var notOnDiskOrUpgradeAvailable = !displayVersion.isAvailableOnDisk || package.state == PackageState.Outdated;
            downloadButton.SetEnabled(enableButton && notOnDiskOrUpgradeAvailable);

            if (!downloadInProgress)
                downloadProgress.Hide();
        }

        private string GetButtonText(PackageAction action, bool inProgress = false, SemVersion version = null)
        {
            var actionText = inProgress ? k_PackageActionInProgressVerbs[(int)action] : k_PackageActionVerbs[(int)action];
            return version == null ? $"{actionText}" : $"{actionText} {version}";
        }

        private void DevelopClick()
        {
            detailError.ClearError();
            PackageDatabase.instance.Embed(package);
            RefreshPackageActionButtons();

            PackageManagerWindowAnalytics.SendEvent("embed", displayVersion?.uniqueId);
        }

        private void DescMoreClick()
        {
            detailDesc.style.maxHeight = float.MaxValue;
            UIUtils.SetElementDisplay(detailDescMore, false);
            UIUtils.SetElementDisplay(detailDescLess, true);
        }

        private void DescLessClick()
        {
            detailDesc.style.maxHeight = 48;
            UIUtils.SetElementDisplay(detailDescMore, true);
            UIUtils.SetElementDisplay(detailDescLess, false);
        }

        private void AuthorClick()
        {
            if (displayVersion == null || !displayVersion.HasTag(PackageTag.AssetStore))
                return;

            ApplicationUtil.instance.OpenURL($"{AssetStoreUtils.instance.assetStoreUrl}/publishers/{displayVersion.publisherId}");
        }

        private void UpdateClick()
        {
            detailError.ClearError();
            PackageDatabase.instance.Install(targetVersion);
            RefreshPackageActionButtons();

            var eventName = package.installedVersion == null ? "installNew" : "installUpdate";
            PackageManagerWindowAnalytics.SendEvent(eventName, displayVersion?.uniqueId);
        }

        /// <summary>
        /// Get a line-separated bullet list of package names
        /// </summary>
        private string GetPackageDashList(IEnumerable<IPackageVersion> versions, int maxListCount = MaxDependentList)
        {
            var shortListed = versions.Take(maxListCount);
            return " - " + string.Join("\n - ", shortListed.Select(p => p.displayName).ToArray());
        }

        /// <summary>
        /// Get the full dependent message string
        /// </summary>
        private string GetDependentMessage(IPackageVersion version, IEnumerable<IPackageVersion> roots, int maxListCount = MaxDependentList)
        {
            var dependentPackages = roots.Where(p => !p.HasTag(PackageTag.BuiltIn)).ToList();
            var dependentModules = roots.Where(p => p.HasTag(PackageTag.BuiltIn)).ToList();

            var packageType = version.HasTag(PackageTag.BuiltIn) ? "built-in package" : "package";
            var prefix = $"This {packageType} is a dependency of the following ";
            var message = string.Format("{0}{1}:\n\n", prefix,  dependentPackages.Any() ? "packages" : "built-in packages");

            if (dependentPackages.Any())
                message += GetPackageDashList(dependentPackages, maxListCount);
            if (dependentPackages.Any() && dependentModules.Any())
                message += "\n\nand the following built-in packages:\n\n";
            if (dependentModules.Any())
                message += GetPackageDashList(dependentModules, maxListCount);

            if (roots.Count() > maxListCount)
                message += "\n\n   ... and more (see console for details) ...";

            var actionType = version.HasTag(PackageTag.BuiltIn) ? "disable" : "remove";
            message += $"\n\nYou will need to remove or disable them before being able to {actionType} this {packageType}.";

            return message;
        }

        private void RemoveClick()
        {
            var roots = PackageDatabase.instance.GetDependentVersions(displayVersion).Where(p => p.isDirectDependency && p.isInstalled).ToList();
            // Only show this message on a package if it is installed by dependency only. This allows it to still be removed from the installed list.
            var showDialog = roots.Any() && !(!displayVersion.HasTag(PackageTag.BuiltIn) && displayVersion.isDirectDependency);
            if (showDialog)
            {
                if (roots.Count > MaxDependentList)
                    Debug.Log(GetDependentMessage(displayVersion, roots, int.MaxValue));

                var message = GetDependentMessage(displayVersion, roots);
                var title = displayVersion.HasTag(PackageTag.BuiltIn) ? "Cannot disable built-in package" : "Cannot remove dependent package";
                EditorUtility.DisplayDialog(title, message, "Ok");

                return;
            }

            if (displayVersion.HasTag(PackageTag.InDevelopment))
            {
                if (!EditorUtility.DisplayDialog("Unity Package Manager", "You will loose all your changes (if any) if you delete a package in development. Are you sure?", "Yes", "No"))
                    return;

                detailError.ClearError();
                PackageDatabase.instance.RemoveEmbedded(package);
                RefreshPackageActionButtons();

                PackageManagerWindowAnalytics.SendEvent("removeEmbedded", displayVersion.uniqueId);

                EditorApplication.delayCall += () =>
                {
                    PackageFilterTab? newFilterTab = null;
                    if (PackageFiltering.instance.currentFilterTab == PackageFilterTab.InDevelopment)
                    {
                        var hasOtherInDevelopment = PackageDatabase.instance.allPackages.Any(p =>
                        {
                            var installed = p.installedVersion;
                            return installed != null && installed.HasTag(PackageTag.InDevelopment) && p.uniqueId != package.uniqueId;
                        });
                        newFilterTab = hasOtherInDevelopment ? PackageFilterTab.InDevelopment : PackageFilterTab.Local;
                    }
                    PackageManagerWindow.SelectPackageAndFilter(displayVersion.uniqueId, newFilterTab, true);
                };
                return;
            }

            var result = 0;
            if (displayVersion.HasTag(PackageTag.BuiltIn))
            {
                if (!PackageManagerPrefs.instance.skipDisableConfirmation)
                {
                    result = EditorUtility.DisplayDialogComplex("Disable Built-In Package",
                        "Are you sure you want to disable this built-in package?",
                        "Disable", "Cancel", "Never ask");
                }
            }
            else
            {
                if (!PackageManagerPrefs.instance.skipRemoveConfirmation)
                {
                    result = EditorUtility.DisplayDialogComplex("Removing Package",
                        "Are you sure you want to remove this package?",
                        "Remove", "Cancel", "Never ask");
                }
            }

            // Cancel
            if (result == 1)
                return;

            // Do not ask again
            if (result == 2)
            {
                if (displayVersion.HasTag(PackageTag.BuiltIn))
                    PackageManagerPrefs.instance.skipDisableConfirmation = true;
                else
                    PackageManagerPrefs.instance.skipRemoveConfirmation = true;
            }

            // Remove
            detailError.ClearError();
            PackageDatabase.instance.Uninstall(package);
            RefreshPackageActionButtons();

            PackageManagerWindowAnalytics.SendEvent("uninstall", displayVersion?.uniqueId);
        }

        private static void ViewOfflineUrl(IPackageVersion version, Func<IPackageVersion, bool, string> getUrl, string messageOnNotFound)
        {
            if (!version.isAvailableOnDisk)
            {
                EditorUtility.DisplayDialog("Unity Package Manager", "This package is not available offline.", "Ok");
                return;
            }
            var offlineUrl = getUrl(version, true);
            if (!string.IsNullOrEmpty(offlineUrl))
                ApplicationUtil.instance.OpenURL(offlineUrl);
            else
                EditorUtility.DisplayDialog("Unity Package Manager", messageOnNotFound, "Ok");
        }

        private static void ViewUrl(IPackageVersion version, Func<IPackageVersion, bool, string> getUrl, string messageOnNotFound)
        {
            if (ApplicationUtil.instance.isInternetReachable)
            {
                var onlineUrl = getUrl(version, false);
                var request = UnityWebRequest.Head(onlineUrl);
                var operation = request.SendWebRequest();
                operation.completed += (op) =>
                {
                    if (request.responseCode != 404)
                    {
                        ApplicationUtil.instance.OpenURL(onlineUrl);
                    }
                    else
                    {
                        ViewOfflineUrl(version, getUrl, messageOnNotFound);
                    }
                };
            }
            else
            {
                ViewOfflineUrl(version, getUrl, messageOnNotFound);
            }
        }

        private void ViewDocClick()
        {
            ViewUrl(displayVersion, UpmPackageDocs.GetDocumentationUrl, "This package does not contain offline documentation.");
        }

        private void ViewChangelogClick()
        {
            ViewUrl(displayVersion, UpmPackageDocs.GetChangelogUrl, "This package does not contain offline changelog.");
        }

        private void ViewLicensesClick()
        {
            ViewUrl(displayVersion, UpmPackageDocs.GetLicensesUrl, "This package does not contain offline licenses.");
        }

        private void ImportClick()
        {
            PackageDatabase.instance.Import(package);
            RefreshImportAndDownloadButtons();
        }

        private void DownloadOrCancelClick()
        {
            if (!ApplicationUtil.instance.isInternetReachable)
            {
                detailError.SetError(new Error(NativeErrorCode.Unknown, "No internet connection"));
                return;
            }

            detailError.ClearError();
            var downloadInProgress = PackageDatabase.instance.IsDownloadInProgress(displayVersion);
            if (downloadInProgress)
                PackageDatabase.instance.AbortDownload(package);
            else
                PackageDatabase.instance.Download(package);

            RefreshImportAndDownloadButtons();
        }

        private VisualElementCache cache { get; set; }

        private VisualElement detailDescContainer { get { return cache.Get<VisualElement>("detailDescContainer"); } }
        private VisualElement detailNameContainer { get { return cache.Get<VisualElement>("detailNameContainer"); } }
        private Label detailName { get { return cache.Get<Label>("detailName"); } }
        private Label detailDesc { get { return cache.Get<Label>("detailDesc"); } }
        private Button detailDescMore { get { return cache.Get<Button>("detailDescMore"); } }
        private Button detailDescLess { get { return cache.Get<Button>("detailDescLess"); } }
        private VisualElement detailLinksContainer { get { return cache.Get<VisualElement>("detailLinksContainer"); } }
        private VisualElement detailLinks { get { return cache.Get<VisualElement>("detailLinks"); } }
        private Alert detailError { get { return cache.Get<Alert>("detailError"); } }
        private ScrollView detailScrollView { get { return cache.Get<ScrollView>("detailScrollView"); } }
        private VisualElement detailContainer { get { return cache.Get<VisualElement>("detail"); } }
        private Label detailTitle { get { return cache.Get<Label>("detailTitle"); } }
        private Label detailVersion { get { return cache.Get<Label>("detailVersion"); } }
        private VisualElement detailDateContainer { get { return cache.Get<VisualElement>("detailDateContainer"); } }
        private Label detailDate { get { return cache.Get<Label>("detailDate"); } }
        private VisualElement detailAuthorContainer { get { return cache.Get<VisualElement>("detailAuthorContainer"); } }
        private Label detailAuthorText { get { return cache.Get<Label>("detailAuthorText"); } }
        private Button detailAuthorLink { get { return cache.Get<Button>("detailAuthorLink"); } }
        private VisualElement customContainer { get { return cache.Get<VisualElement>("detailCustomContainer"); } }
        private PackageSampleList sampleList { get { return cache.Get<PackageSampleList>("detailSampleList"); } }
        private PackageDependencies dependencies { get {return cache.Get<PackageDependencies>("detailDependencies");} }
        private VisualElement packageToolbarContainer { get {return cache.Get<VisualElement>("toolbarContainer");} }
        private VisualElement packageToolbarLeftArea { get {return cache.Get<VisualElement>("leftItems");} }
        internal Button updateButton { get { return cache.Get<Button>("update"); } }
        internal Button developButton { get { return cache.Get<Button>("develop"); } }
        internal Button removeButton { get { return cache.Get<Button>("remove"); } }
        private Button importButton { get { return cache.Get<Button>("import"); } }
        private Button downloadButton { get { return cache.Get<Button>("download"); } }
        private VisualElement editPackageManifestButton { get { return cache.Get<VisualElement>("editPackageManifest"); } }
        private ProgressBar downloadProgress { get { return cache.Get<ProgressBar>("downloadProgress"); } }
        private VisualElement detailCategories { get { return cache.Get<VisualElement>("detailCategories"); } }
        private VisualElement detailUnityVersionsContainer { get { return cache.Get<VisualElement>("detailUnityVersionsContainer"); } }
        private Label detailUnityVersions { get { return cache.Get<Label>("detailUnityVersions"); } }
        private VisualElement detailSizesContainer { get { return cache.Get<VisualElement>("detailSizesContainer"); } }
        private VisualElement detailSizes { get { return cache.Get<VisualElement>("detailSizes"); } }
        private VisualElement detailImagesContainer { get { return cache.Get<VisualElement>("detailImagesContainer"); } }
        private VisualElement detailImages { get { return cache.Get<VisualElement>("detailImages"); } }
        internal Label GetTagLabel(PackageTag tag) { return cache.Get<Label>("tag" + tag); }
    }
}
