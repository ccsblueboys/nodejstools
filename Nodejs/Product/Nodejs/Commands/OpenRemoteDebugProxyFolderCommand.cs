// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudioTools;
using Microsoft.NodejsTools.Project;

namespace Microsoft.NodejsTools.Commands
{
    internal sealed class OpenRemoteDebugProxyFolderCommand : Command
    {
        private const string remoteDebugJsFileName = "RemoteDebug.js";

        private static string RemoteDebugProxyFolder => Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "RemoteDebug");

        public override void DoCommand(object sender, EventArgs args)
        {
            // Open explorer to folder
            var remoteDebugProxyFolder = RemoteDebugProxyFolder;
            if (string.IsNullOrWhiteSpace(remoteDebugProxyFolder))
            {
                MessageBox.Show(Resources.RemoteDebugProxyFolderDoesNotExist, SR.ProductName);
                return;
            }

            var filePath = Path.Combine(remoteDebugProxyFolder, remoteDebugJsFileName);
            if (!File.Exists(filePath))
            {
                MessageBox.Show(string.Format(CultureInfo.CurrentCulture, Resources.RemoteDebugProxyFileDoesNotExist, filePath), SR.ProductName);
            }
            else
            {
                Process.Start("explorer", string.Format(CultureInfo.InvariantCulture, "/e,/select,{0}", filePath));
            }
        }

        public override int CommandId => (int)PkgCmdId.cmdidOpenRemoteDebugProxyFolder;
    }
}
