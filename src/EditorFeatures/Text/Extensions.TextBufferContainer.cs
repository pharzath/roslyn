﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Text
{
    public static partial class Extensions
    {
        /// <summary>
        /// ITextBuffer implementation of SourceTextContainer
        /// </summary>
        internal class TextBufferContainer : SourceTextContainer
        {
            private readonly WeakReference<ITextBuffer> _weakEditorBuffer;
            private readonly object _gate = new object();

            private event EventHandler<TextChangeEventArgs> EtextChanged;
            private SourceText _currentText;

            private TextBufferContainer(Workspace workspace, ITextBuffer editorBuffer)
            {
                Contract.ThrowIfNull(editorBuffer);

                _weakEditorBuffer = new WeakReference<ITextBuffer>(editorBuffer);
                _currentText = SnapshotSourceText.From(workspace, editorBuffer.CurrentSnapshot, this);
            }

            /// <summary>
            /// A weak map of all Editor ITextBuffers and their associated SourceTextContainer
            /// </summary>
            private static readonly ConditionalWeakTable<ITextBuffer, TextBufferContainer> s_textContainerMap = new ConditionalWeakTable<ITextBuffer, TextBufferContainer>();
            private static readonly ConditionalWeakTable<ITextBuffer, TextBufferContainer>.CreateValueCallback s_createContainerWithoutWorkspaceCallback = CreateContainer;

            public static TextBufferContainer From(Workspace workspace, ITextBuffer buffer)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (workspace is null)
                {
                    return s_textContainerMap.GetValue(buffer, s_createContainerWithoutWorkspaceCallback);
                }

                if (!s_textContainerMap.TryGetValue(buffer, out var container))
                {
                    // Avoid capturing 'workspace' on the fast path
                    var workspaceCopy = workspace;
                    container = s_textContainerMap.GetValue(buffer, key => new TextBufferContainer(workspace, key));
                }

                return container;
            }

            private static TextBufferContainer CreateContainer(ITextBuffer editorBuffer)
            {
                return new TextBufferContainer(workspace: null, editorBuffer);
            }

            public ITextBuffer TryFindEditorTextBuffer()
                => _weakEditorBuffer.GetTarget();

            public override SourceText CurrentText
            {
                get
                {
                    var editorBuffer = this.TryFindEditorTextBuffer();
                    return editorBuffer != null
                        ? editorBuffer.CurrentSnapshot.AsText()
                        : _currentText;
                }
            }

            public override event EventHandler<TextChangeEventArgs> TextChanged
            {
                add
                {
                    lock (_gate)
                    {
                        var textBuffer = this.TryFindEditorTextBuffer();
                        if (this.EtextChanged == null && textBuffer != null)
                        {
                            textBuffer.ChangedHighPriority += this.OnTextContentChanged;
                        }

                        this.EtextChanged += value;
                    }
                }

                remove
                {
                    lock (_gate)
                    {
                        this.EtextChanged -= value;

                        var textBuffer = this.TryFindEditorTextBuffer();
                        if (this.EtextChanged == null && textBuffer != null)
                        {
                            textBuffer.ChangedHighPriority -= this.OnTextContentChanged;
                        }
                    }
                }
            }

            private void OnTextContentChanged(object sender, TextContentChangedEventArgs args)
            {
                var changed = this.EtextChanged;
                if (changed != null && args.Changes.Count != 0)
                {
                    // this should convert given editor snapshots to roslyn forked snapshots
                    var oldText = (SnapshotSourceText)args.Before.AsText();
                    var newText = SnapshotSourceText.From(oldText.Workspace, args.After);
                    _currentText = newText;

                    var changes = ImmutableArray.CreateRange(args.Changes.Select(c => new TextChangeRange(new TextSpan(c.OldSpan.Start, c.OldSpan.Length), c.NewLength)));
                    var eventArgs = new TextChangeEventArgs(oldText, newText, changes);
                    this.LastEventArgs = eventArgs;
                    changed(sender, eventArgs);
                }
            }

            // These are the event args that were last sent from this text container when the text
            // content may have changed.
            public TextChangeEventArgs LastEventArgs { get; private set; }
        }
    }
}
