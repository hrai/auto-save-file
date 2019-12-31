﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using Task = System.Threading.Tasks.Task;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AutoSaveFileTests")]
namespace AutoSaveFile
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(AutoSaveFilePackage.PackageGuidString)]
    [ProvideService(typeof(AutoSaveFilePackage), IsAsyncQueryable = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasMultipleProjects_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasSingleProject_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(OptionPageGrid), "Auto Save File", "General", 0, 0, true)]
    public sealed class AutoSaveFilePackage : AsyncPackage
    {
        /// <summary>
        /// AutoSaveFilePackage GUID string.
        /// </summary>
        public const string PackageGuidString = "d520a8f3-cfd5-4ba3-a154-66b97d118c91";

        private TextEditorEvents _dteEditorEvents;
        private WindowEvents _dteWindowEvents;
        private Stack<CancellationTokenSource> _stack;
        private Helper _helper;

        #region Package Members

        /// <summary>
        /// Initialisation of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the Initialisation code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for Initialisation cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package Initialisation, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            // When Initialised asynchronously, the current thread may be a background thread at this point.
            // Do any Initialisation that requires the UI thread after switching to the UI thread.
            //await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _helper = new Helper();

            GetLogger().LogInformation(GetPackageName(), "Initialising.");
            await base.InitializeAsync(cancellationToken, progress);

            try
            {
                var dte = (DTE)await this.GetServiceAsync(typeof(DTE));
                var _dteEvents = dte.Events;

                _dteEditorEvents = _dteEvents.TextEditorEvents;
                _dteWindowEvents = _dteEvents.WindowEvents;

                _dteEditorEvents.LineChanged += OnLineChanged;
                _dteWindowEvents.WindowActivated += OnWindowActivated;

                _stack = new Stack<CancellationTokenSource>();

                GetLogger().LogInformation(GetPackageName(), "Initialised.");
            }
            catch (Exception exception)
            {
                GetLogger().LogError(GetPackageName(), "Exception during initialisation", exception);
            }
        }

        private void OnWindowActivated(Window gotFocus, Window lostFocus)
        {
            if (lostFocus != null)
            {
                Save(lostFocus);
            }
        }

        private void Save(Window window)
        {
            window.Project?.Save();
            window.Document?.Save();
        }

        private static string GetChangedText(TextPoint startPoint, TextPoint endPoint)
        {
            EditPoint editPoint = startPoint.CreateEditPoint();
            var content = editPoint.GetText(endPoint);
            return content;
        }

        public bool IsLastModifiedCharacterPeriod(string changedText)
        {
            return changedText.LastIndexOf('.') == changedText.Length - 1;
        }

        private void OnLineChanged(TextPoint startPoint, TextPoint endPoint, int Hint)
        {
            var changedText = GetChangedText(startPoint, endPoint);
            if (changedText.Length != 0 && IsLastModifiedCharacterPeriod(changedText))
            {
                CancelPreviousSaveTask();
                return;
            }

            CancelPreviousSaveTask();

            var _cancellationTokenSource = new CancellationTokenSource();
            _stack.Push(_cancellationTokenSource);

            Task.Run(async () =>
                         {
                             try
                             {
                                 await WaitForUserConfiguredDelayAsync();

                                 if (!_cancellationTokenSource.IsCancellationRequested)
                                 {
                                     var dte = (DTE)await this.GetServiceAsync(typeof(DTE));
                                     SaveDocument(dte);
                                 }
                             }
                             catch (Exception exception)
                             {
                                 GetLogger().LogError(GetPackageName(), "Exception during line change event handling", exception);
                             }
                         });
        }

        private async Task WaitForUserConfiguredDelayAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

            var optionsPage = (OptionPageGrid)GetDialogPage(typeof(OptionPageGrid));
            var timeDelayInSeconds = optionsPage.TimeDelay;

            await Task.Delay(1000 * timeDelayInSeconds);
        }

        private void CancelPreviousSaveTask()
        {
            if (_stack.Any())
            {
                _stack.Pop().Cancel();
            }
        }

        private void SaveDocument(DTE dte)
        {
            var window = dte.ActiveWindow;
            var windowType = window.Kind;

            if (windowType == "Document")
            {
                var fileType = _helper.GetFileType(window);
                var optionsPage = (OptionPageGrid)GetDialogPage(typeof(OptionPageGrid));
                var ignoredFileTypes = optionsPage.IgnoredFileTypes;

                if (ignoredFileTypes != null && !ignoredFileTypes.Contains(fileType))
                {
                    Save(window);
                }
            }
        }


        private string GetPackageName() => nameof(AutoSaveFilePackage);

        private IVsActivityLog GetLogger()
        {
            return this.GetService(typeof(SVsActivityLog)) as IVsActivityLog ?? new NullLogger();
        }

        #endregion
    }

    internal class Helper
    {
        internal string GetFileType(Window window)
        {
            var documentFullName = window.Document?.FullName;

            if (documentFullName == null)
                documentFullName = window.Project?.FullName;

            if (Path.HasExtension(documentFullName))
                return Path.GetExtension(documentFullName).Replace(".", "");

            return "";
        }
    }
}
