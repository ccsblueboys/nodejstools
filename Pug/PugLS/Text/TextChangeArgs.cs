// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.VisualStudio.Pug
{
    /// <summary>
    /// Text change event arguments. This class abstracts text change information 
    /// allowing code that handles text changes to use <seealso cref="ITextProvider"/>
    /// rather than Visual Studio ITextBuffer or some other editor specific types.
    /// </summary>
    [ExcludeFromCodeCoverage]
    internal class TextChangeEventArgs : EventArgs
    {
        /// <summary>
        /// Start position of the change
        /// </summary>
        public int Start { get; private set; }

        /// <summary>
        /// Length of the fragment that was deleted or replaced.
        /// Zero if operation is 'insert' or 'paste' without selection.
        /// </summary>
        public int OldLength { get; private set; }

        /// <summary>
        /// Length of the new fragment. Zero if operation is 'delete'.
        /// </summary>
        public int NewLength { get; private set; }

        /// <summary>
        /// Snaphot before the change
        /// </summary>
        public ITextProvider OldText { get; private set; }

        /// <summary>
        /// Snapshot after the change
        /// </summary>
        public ITextProvider NewText { get; private set; }

        public TextChangeEventArgs(int start, int oldLength, int newLength)
            : this(start, oldLength, newLength, null, null)
        {
        }

        [DebuggerStepThrough]
        public TextChangeEventArgs(int start, int oldLength, int newLength, ITextProvider oldText, ITextProvider newText)
        {
            this.Start = start;
            this.OldLength = oldLength;
            this.NewLength = newLength;
            this.OldText = oldText;
            this.NewText = newText;
        }
    }
}

