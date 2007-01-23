﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Mike Krüger" email="mike@icsharpcode.net"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop
{
	public static class FileService
	{
		static bool serviceInitialized;
		
		#region RecentOpen
		static RecentOpen recentOpen = null;
		
		public static RecentOpen RecentOpen {
			get {
				if (recentOpen == null) {
					recentOpen = RecentOpen.FromXmlElement(PropertyService.Get("RecentOpen", new Properties()));
				}
				return recentOpen;
			}
		}
		
		static void ProjectServiceSolutionLoaded(object sender, SolutionEventArgs e)
		{
			RecentOpen.AddLastProject(e.Solution.FileName);
		}
		#endregion
		
		public static void Unload()
		{
			if (recentOpen != null) {
				PropertyService.Set("RecentOpen", recentOpen.ToProperties());
			}
			ProjectService.SolutionLoaded -= ProjectServiceSolutionLoaded;
			serviceInitialized = false;
		}
		
		internal static void InitializeService()
		{
			if (!serviceInitialized) {
				ProjectService.SolutionLoaded += ProjectServiceSolutionLoaded;
				serviceInitialized = true;
			}
		}
		
		#region OpenedFile
		static Dictionary<string, OpenedFile> openedFileDict = new Dictionary<string, OpenedFile>(StringComparer.InvariantCultureIgnoreCase);
		
		/// <summary>
		/// Gets a collection containing all currently opened files.
		/// </summary>
		public static ICollection<OpenedFile> OpenedFiles {
			get {
				return openedFileDict.Values;
			}
		}
		
		/// <summary>
		/// Gets an opened file, or returns null if the file is not opened.
		/// </summary>
		public static OpenedFile GetOpenedFile(string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			
			fileName = FileUtility.NormalizePath(fileName);
			OpenedFile file;
			openedFileDict.TryGetValue(fileName, out file);
			return file;
		}
		
		/// <summary>
		/// Gets or creates an opened file.
		/// Warning: the opened file will be a file without any views attached.
		/// Make sure to attach a view to it, or call CloseIfAllViewsClosed on the OpenedFile if you
		/// are not sure that views were attached to it.
		/// </summary>
		internal static OpenedFile GetOrCreateOpenedFile(string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			
			fileName = FileUtility.NormalizePath(fileName);
			OpenedFile file;
			if (!openedFileDict.TryGetValue(fileName, out file)) {
				openedFileDict[fileName] = file = new OpenedFile(fileName);
			}
			return file;
		}
		
		/// <summary>
		/// Attaches the view to the opened file. Creates a new OpenedFile instance if necessary.
		/// </summary>
		public static OpenedFile AttachToOpenedFile(string fileName, IViewContent view)
		{
			if (view == null)
				throw new ArgumentNullException("view");
			
			OpenedFile file = GetOrCreateOpenedFile(fileName);
			file.RegisterView(view);
			return file;
		}
		
		/// <summary>Called by OpenedFile.set_FileName to update the dictionary.</summary>
		internal static void OpenedFileFileNameChange(OpenedFile file, string oldName, string newName)
		{
			if (oldName == null) return; // File just created with NewFile where name is being initialized.
			
			LoggingService.Debug("OpenedFileFileNameChange: " + oldName + " => " + newName);
			
			Debug.Assert(openedFileDict[oldName] == file);
			Debug.Assert(!openedFileDict.ContainsKey(newName));
			
			openedFileDict.Remove(oldName);
			openedFileDict[newName] = file;
		}
		
		/// <summary>Called by OpenedFile.UnregisterView to update the dictionary.</summary>
		internal static void OpenedFileClosed(OpenedFile file)
		{
			Debug.Assert(openedFileDict[file.FileName] == file);
			openedFileDict.Remove(file.FileName);
			LoggingService.Debug("OpenedFileClosed: " + file.FileName);
		}
		#endregion
		
		/// <summary>
		/// Checks if the file name is valid <b>and shows a MessageBox if it is not valid</b>.
		/// Do not use in non-UI methods.
		/// </summary>
		public static bool CheckFileName(string fileName)
		{
			if (FileUtility.IsValidFileName(fileName))
				return true;
			MessageService.ShowMessage(StringParser.Parse("${res:ICSharpCode.SharpDevelop.Commands.SaveFile.InvalidFileNameError}", new string[,] {{"FileName", fileName}}));
			return false;
		}
		
		/// <summary>
		/// Checks that a single directory name is valid.
		/// </summary>
		/// <param name="name">A single directory name not the full path</param>
		public static bool CheckDirectoryName(string name)
		{
			if (FileUtility.IsValidDirectoryName(name))
				return true;
			MessageService.ShowMessage(StringParser.Parse("${res:ICSharpCode.SharpDevelop.Commands.SaveFile.InvalidFileNameError}", new string[,] {{"FileName", name}}));
			return false;
		}
		
		internal sealed class LoadFileWrapper
		{
			IDisplayBinding binding;
			
			public LoadFileWrapper(IDisplayBinding binding)
			{
				this.binding = binding;
			}
			
			public void Invoke(string fileName)
			{
				OpenedFile file = FileService.GetOrCreateOpenedFile(fileName);
				IViewContent newContent = binding.CreateContentForFile(file);
				if (newContent != null) {
					DisplayBindingService.AttachSubWindows(newContent, false);
					WorkbenchSingleton.Workbench.ShowView(newContent);
				}
				file.CloseIfAllViewsClosed();
			}
		}
		
		public static bool IsOpen(string fileName)
		{
			return GetOpenFile(fileName) != null;
		}
		
		public static IViewContent OpenFile(string fileName)
		{
			fileName = FileUtility.NormalizePath(fileName);
			LoggingService.Info("Open file " + fileName);
			
			IViewContent viewContent = GetOpenFile(fileName);
			if (viewContent != null) {
				viewContent.WorkbenchWindow.SelectWindow();
				return viewContent;
			}
			
			IDisplayBinding binding = DisplayBindingService.GetBindingPerFileName(fileName);
			
			if (binding != null) {
				if (FileUtility.ObservedLoad(new NamedFileOperationDelegate(new LoadFileWrapper(binding).Invoke), fileName) == FileOperationResult.OK) {
					FileService.RecentOpen.AddLastFile(fileName);
				}
			} else {
				throw new ApplicationException("Cannot open " + fileName + ", no display codon found.");
			}
			return GetOpenFile(fileName);
		}
		
		/// <summary>
		/// Opens a new unsaved file.
		/// </summary>
		/// <param name="defaultName">The (unsaved) name of the to open</param>
		/// <param name="content">Content of the file to create</param>
		public static IViewContent NewFile(string defaultName, string content)
		{
			return NewFile(defaultName, ParserService.DefaultFileEncoding.GetBytes(content));
		}
		
		/// <summary>
		/// Opens a new unsaved file.
		/// </summary>
		/// <param name="defaultName">The (unsaved) name of the to open</param>
		/// <param name="content">Content of the file to create</param>
		public static IViewContent NewFile(string defaultName, byte[] content)
		{
			if (defaultName == null)
				throw new ArgumentNullException("defaultName");
			if (content == null)
				throw new ArgumentNullException("content");
			
			IDisplayBinding binding = DisplayBindingService.GetBindingPerFileName(defaultName);
			
			if (binding != null) {
				OpenedFile file = new OpenedFile(content);
				file.FileName = file.GetHashCode() + "/" + defaultName;
				openedFileDict[file.FileName] = file;
				
				IViewContent newContent = binding.CreateContentForFile(file);
				if (newContent == null) {
					LoggingService.Warn("Created view content was null - DefaultName:" + defaultName);
					return null;
				}
				
				DisplayBindingService.AttachSubWindows(newContent, false);
				
				WorkbenchSingleton.Workbench.ShowView(newContent);
				return newContent;
			} else {
				throw new ApplicationException("Can't create display binding for file " + defaultName);
			}
		}
		
		public static IList<string> GetOpenFiles()
		{
			List<string> fileNames = new List<string>();
			foreach (IViewContent content in WorkbenchSingleton.Workbench.ViewContentCollection) {
				string contentName = content.PrimaryFileName;
				if (contentName != null)
					fileNames.Add(contentName);
			}
			return fileNames;
		}
		
		public static IViewContent GetOpenFile(string fileName)
		{
			if (fileName != null && fileName.Length > 0) {
				foreach (IViewContent content in WorkbenchSingleton.Workbench.ViewContentCollection) {
					string contentName = content.PrimaryFileName;
					if (contentName != null) {
						if (FileUtility.IsEqualFileName(fileName, contentName))
							return content;
					}
				}
			}
			return null;
		}
		
		/// <summary>
		/// Removes a file, raising the appropriate events. This method may show message boxes.
		/// </summary>
		public static void RemoveFile(string fileName, bool isDirectory)
		{
			FileCancelEventArgs eargs = new FileCancelEventArgs(fileName, isDirectory);
			OnFileRemoving(eargs);
			if (eargs.Cancel)
				return;
			if (!eargs.OperationAlreadyDone) {
				if (isDirectory) {
					try {
						if (Directory.Exists(fileName)) {
							Directory.Delete(fileName, true);
						}
					} catch (Exception e) {
						MessageService.ShowError(e, "Can't remove directory " + fileName);
//					return;
					}
				} else {
					try {
						if (File.Exists(fileName)) {
							File.Delete(fileName);
						}
					} catch (Exception e) {
						MessageService.ShowError(e, "Can't remove file " + fileName);
//					return;
					}
				}
			}
			OnFileRemoved(new FileEventArgs(fileName, isDirectory));
		}
		
		/// <summary>
		/// Renames or moves a file, raising the appropriate events. This method may show message boxes.
		/// </summary>
		public static bool RenameFile(string oldName, string newName, bool isDirectory)
		{
			if (FileUtility.IsEqualFileName(oldName, newName))
				return false;
			FileRenamingEventArgs eargs = new FileRenamingEventArgs(oldName, newName, isDirectory);
			OnFileRenaming(eargs);
			if (eargs.Cancel)
				return false;
			if (!eargs.OperationAlreadyDone) {
				try {
					if (isDirectory && Directory.Exists(oldName)) {
						
						if (Directory.Exists(newName)) {
							MessageService.ShowMessage(StringParser.Parse("${res:Gui.ProjectBrowser.FileInUseError}"));
							return false;
						}
						Directory.Move(oldName, newName);
						
					} else if (File.Exists(oldName)) {
						if (File.Exists(newName)) {
							MessageService.ShowMessage(StringParser.Parse("${res:Gui.ProjectBrowser.FileInUseError}"));
							return false;
						}
						File.Move(oldName, newName);
					}
				} catch (Exception e) {
					if (isDirectory) {
						MessageService.ShowError(e, "Can't rename directory " + oldName);
					} else {
						MessageService.ShowError(e, "Can't rename file " + oldName);
					}
					return false;
				}
			}
			OnFileRenamed(new FileRenameEventArgs(oldName, newName, isDirectory));
			return true;
		}
		
		/// <summary>
		/// Opens the specified file and jumps to the specified file position.
		/// Warning: Unlike parser coordinates, line and column are 0-based.
		/// </summary>
		public static IViewContent JumpToFilePosition(string fileName, int line, int column)
		{
			if (fileName == null || fileName.Length == 0) {
				return null;
			}
			IViewContent content = OpenFile(fileName);
			if (content is IPositionable) {
				// TODO: enable jumping to a particular view
				((IPositionable)content).JumpTo(Math.Max(0, line), Math.Max(0, column));
			}
			NavigationService.Log(content.BuildNavPoint());
			return content;
		}
		
		/// <summary>
		/// Creates a FolderBrowserDialog that will initially select the 
		/// specified folder. If the folder does not exist then the default
		/// behaviour of the FolderBrowserDialog is used where it selects the
		/// desktop folder.
		/// </summary>
		public static FolderBrowserDialog CreateFolderBrowserDialog(string description, string selectedPath)
		{
			FolderBrowserDialog dialog = new FolderBrowserDialog();
			dialog.Description = StringParser.Parse(description);
			if (selectedPath != null && selectedPath.Length > 0 && Directory.Exists(selectedPath)) {
				dialog.RootFolder = Environment.SpecialFolder.MyComputer;
				dialog.SelectedPath = selectedPath;
			}
			return dialog;
		}
		
		/// <summary>
		/// Creates a FolderBrowserDialog that will initially select the 
		/// desktop folder.
		/// </summary>
		public static FolderBrowserDialog CreateFolderBrowserDialog(string description)
		{
			return CreateFolderBrowserDialog(description, null);
		}
		
		static void OnFileRemoved(FileEventArgs e)
		{
			if (FileRemoved != null) {
				FileRemoved(null, e);
			}
		}
		
		static void OnFileRemoving(FileCancelEventArgs e)
		{
			if (FileRemoving != null) {
				FileRemoving(null, e);
			}
		}
		
		static void OnFileRenamed(FileRenameEventArgs e)
		{
			if (FileRenamed != null) {
				FileRenamed(null, e);
			}
		}
		
		static void OnFileRenaming(FileRenamingEventArgs e) {
			if (FileRenaming != null) {
				FileRenaming(null, e);
			}
		}
		
		public static bool FireFileReplacing(string fileName, bool isDirectory)
		{
			FileCancelEventArgs e = new FileCancelEventArgs(fileName, isDirectory);
			if (FileReplacing != null) {
				FileReplacing(null, e);
			}
			return !e.Cancel;
		}
		
		public static void FireFileReplaced(string fileName, bool isDirectory)
		{
			if (FileReplaced != null) {
				FileReplaced(null, new FileEventArgs(fileName, isDirectory));
			}
		}
		
		public static void FireFileCreated(string fileName)
		{
			if (FileCreated != null) {
				FileCreated(null, new FileEventArgs(fileName, false));
			}
		}
		
		public static event EventHandler<FileEventArgs> FileCreated;
		
		public static event EventHandler<FileRenamingEventArgs> FileRenaming;
		public static event EventHandler<FileRenameEventArgs> FileRenamed;
		
		public static event EventHandler<FileCancelEventArgs> FileRemoving;
		public static event EventHandler<FileEventArgs> FileRemoved;
		
		public static event EventHandler<FileCancelEventArgs> FileReplacing;
		public static event EventHandler<FileEventArgs> FileReplaced;
	}
}
