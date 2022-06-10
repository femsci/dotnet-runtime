﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using Internal.IL;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.IO.Compression;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System.Diagnostics.CodeAnalysis;
using ILCompiler.Reflection.ReadyToRun;
using Microsoft.Diagnostics.Tools.Pgo;
using Internal.Pgo;
using ILCompiler.IBC;
using ILCompiler;

namespace Microsoft.Diagnostics.Tools.Pgo
{
    static class MibcEmitter
    {
        class MIbcGroup : IPgoEncodedValueEmitter<TypeSystemEntityOrUnknown, TypeSystemEntityOrUnknown>
        {
            private static int s_emitCount = 0;

            public MIbcGroup(string name, TypeSystemMetadataEmitter emitter)
            {
                _buffer = new BlobBuilder();
                _il = new InstructionEncoder(_buffer);
                _name = name;
                _emitter = emitter;
            }

            private BlobBuilder _buffer;
            private InstructionEncoder _il;
            private string _name;
            private TypeSystemMetadataEmitter _emitter;

            public void AddProcessedMethodData(MethodProfileData processedMethodData)
            {
                MethodDesc method = processedMethodData.Method;

                // Format is 
                // ldtoken method
                // variable amount of extra metadata about the method, Extension data is encoded via ldstr "id"
                // pop

                // Extensions generated by this emitter:
                //
                // ldstr "ExclusiveWeight"
                // Any ldc.i4 or ldc.r4 or ldc.r8 instruction to indicate the exclusive weight
                //
                // ldstr "WeightedCallData"
                // ldc.i4 <Count of methods called>
                // Repeat <Count of methods called times>
                //  ldtoken <Method called from this method>
                //  ldc.i4 <Weight associated with calling the <Method called from this method>>
                //
                // ldstr "InstrumentationDataStart"
                // Encoded ints and longs, using ldc.i4, and ldc.i8 instructions as well as ldtoken <type> instructions
                // ldstr "InstrumentationDataEnd" as a terminator
                try
                {
                    EntityHandle methodHandle = _emitter.GetMethodRef(method);
                    _il.OpCode(ILOpCode.Ldtoken);
                    _il.Token(methodHandle);
                    if (processedMethodData.ExclusiveWeight != 0)
                    {
                        _il.LoadString(_emitter.GetUserStringHandle("ExclusiveWeight"));
                        if (((double)(int)processedMethodData.ExclusiveWeight) == processedMethodData.ExclusiveWeight)
                            _il.LoadConstantI4((int)processedMethodData.ExclusiveWeight);
                        else
                            _il.LoadConstantR8(processedMethodData.ExclusiveWeight);
                    }
                    if ((processedMethodData.CallWeights != null) && processedMethodData.CallWeights.Count > 0)
                    {
                        _il.LoadString(_emitter.GetUserStringHandle("WeightedCallData"));
                        _il.LoadConstantI4(processedMethodData.CallWeights.Count);
                        foreach (var entry in processedMethodData.CallWeights)
                        {
                            EntityHandle calledMethod = _emitter.GetMethodRef(entry.Key);
                            _il.OpCode(ILOpCode.Ldtoken);
                            _il.Token(calledMethod);
                            _il.LoadConstantI4(entry.Value);
                        }
                    }
                    if (processedMethodData.SchemaData != null)
                    {
                        _il.LoadString(_emitter.GetUserStringHandle("InstrumentationDataStart"));
                        PgoProcessor.EncodePgoData<TypeSystemEntityOrUnknown, TypeSystemEntityOrUnknown>(processedMethodData.SchemaData, this, true);
                    }
                    _il.OpCode(ILOpCode.Pop);
                }
                catch (Exception ex)
                {
                    Program.PrintWarning($"Exception {ex} while attempting to generate method lists");
                }
            }

            public MethodDefinitionHandle EmitMethod()
            {
                s_emitCount++;
                string basicName = "Assemblies_" + _name;
                if (_name.Length > 200)
                    basicName = basicName.Substring(0, 200); // Cap length of name at 200, which is reasonably small.

                string methodName = basicName + "_" + s_emitCount.ToString(CultureInfo.InvariantCulture);
                return _emitter.AddGlobalMethod(methodName, _il, 8);
            }

            bool IPgoEncodedValueEmitter<TypeSystemEntityOrUnknown, TypeSystemEntityOrUnknown>.EmitDone()
            {
                _il.LoadString(_emitter.GetUserStringHandle("InstrumentationDataEnd"));
                return true;
            }

            void IPgoEncodedValueEmitter<TypeSystemEntityOrUnknown, TypeSystemEntityOrUnknown>.EmitLong(long value, long previousValue)
            {
                if ((value <= int.MaxValue) && (value >= int.MinValue))
                {
                    _il.LoadConstantI4(checked((int)value));
                }
                else
                {
                    _il.LoadConstantI8(value);
                }
            }

            void IPgoEncodedValueEmitter<TypeSystemEntityOrUnknown, TypeSystemEntityOrUnknown>.EmitType(TypeSystemEntityOrUnknown type, TypeSystemEntityOrUnknown previousValue)
            {
                if (type.AsType != null)
                {
                    _il.OpCode(ILOpCode.Ldtoken);
                    _il.Token(_emitter.GetTypeRef(type.AsType));
                }
                else
                {

                    _il.LoadConstantI4(type.AsUnknown & 0x00FFFFFF);
                }
            }

            void IPgoEncodedValueEmitter<TypeSystemEntityOrUnknown, TypeSystemEntityOrUnknown>.EmitMethod(TypeSystemEntityOrUnknown method, TypeSystemEntityOrUnknown previousValue)
            {
                if (method.AsMethod != null)
                {
                    _il.OpCode(ILOpCode.Ldtoken);
                    _il.Token(_emitter.GetMethodRef(method.AsMethod));
                }
                else
                {

                    _il.LoadConstantI4(method.AsUnknown & 0x00FFFFFF);
                }
            }

        }

        private static string GetTypeDefiningAssembly(TypeDesc type)
        {
            return ((MetadataType)type).Module.Assembly.GetName().Name;
        }

        private static void AddAssembliesAssociatedWithType(TypeDesc type, HashSet<string> assemblies, out string definingAssembly)
        {
            definingAssembly = GetTypeDefiningAssembly(type);
            assemblies.Add(definingAssembly);
            AddAssembliesAssociatedWithType(type, assemblies);
        }

        private static void AddAssembliesAssociatedWithType(TypeDesc type, HashSet<string> assemblies)
        {
            if (type.IsPrimitive)
                return;

            if (type.Context.IsCanonicalDefinitionType(type, CanonicalFormKind.Any))
                return;

            if (type.IsParameterizedType)
            {
                AddAssembliesAssociatedWithType(type.GetParameterType(), assemblies);
            }
            else
            {
                assemblies.Add(GetTypeDefiningAssembly(type));
                foreach (var instantiationType in type.Instantiation)
                {
                    AddAssembliesAssociatedWithType(instantiationType, assemblies);
                }
            }
        }

        private static void AddAssembliesAssociatedWithMethod(MethodDesc method, HashSet<string> assemblies, out string definingAssembly)
        {
            AddAssembliesAssociatedWithType(method.OwningType, assemblies, out definingAssembly);
            foreach (var instantiationType in method.Instantiation)
            {
                AddAssembliesAssociatedWithType(instantiationType, assemblies);
            }
        }

        public static int GenerateMibcFile(TypeSystemContext tsc, FileInfo outputFileName, IEnumerable<MethodProfileData> methodsToAttemptToPlaceIntoProfileData, bool validate, bool uncompressed)
        {
            TypeSystemMetadataEmitter emitter = new TypeSystemMetadataEmitter(new AssemblyName(outputFileName.Name), tsc);
            emitter.InjectSystemPrivateCanon();
            emitter.AllowUseOfAddGlobalMethod();

            SortedDictionary<string, MIbcGroup> groups = new SortedDictionary<string, MIbcGroup>();
            StringBuilder mibcGroupNameBuilder = new StringBuilder();
            HashSet<string> assembliesAssociatedWithMethod = new HashSet<string>();

            foreach (var entry in methodsToAttemptToPlaceIntoProfileData)
            {
                MethodDesc method = entry.Method;
                assembliesAssociatedWithMethod.Clear();
                AddAssembliesAssociatedWithMethod(method, assembliesAssociatedWithMethod, out string definingAssembly);

                string[] assemblyNames = new string[assembliesAssociatedWithMethod.Count];
                int i = 1;
                assemblyNames[0] = definingAssembly;

                foreach (string s in assembliesAssociatedWithMethod)
                {
                    if (s.Equals(definingAssembly))
                        continue;
                    assemblyNames[i++] = s;
                }

                // Always keep the defining assembly as the first name
                Array.Sort(assemblyNames, 1, assemblyNames.Length - 1);
                mibcGroupNameBuilder.Clear();
                foreach (string s in assemblyNames)
                {
                    mibcGroupNameBuilder.Append(s);
                    mibcGroupNameBuilder.Append(';');
                }

                string mibcGroupName = mibcGroupNameBuilder.ToString();
                if (!groups.TryGetValue(mibcGroupName, out MIbcGroup mibcGroup))
                {
                    mibcGroup = new MIbcGroup(mibcGroupName, emitter);
                    groups.Add(mibcGroupName, mibcGroup);
                }
                mibcGroup.AddProcessedMethodData(entry);
            }

            var buffer = new BlobBuilder();
            var il = new InstructionEncoder(buffer);

            foreach (var entry in groups)
            {
                il.LoadString(emitter.GetUserStringHandle(entry.Key));
                il.OpCode(ILOpCode.Ldtoken);
                il.Token(entry.Value.EmitMethod());
                il.OpCode(ILOpCode.Pop);
            }

            emitter.AddGlobalMethod("AssemblyDictionary", il, 8);
            MemoryStream peFile = new MemoryStream();
            emitter.SerializeToStream(peFile);
            peFile.Position = 0;

            if (outputFileName.Exists)
            {
                outputFileName.Delete();
            }

            if (uncompressed)
            {
                using (FileStream file = new FileStream(outputFileName.FullName, FileMode.Create))
                {
                    peFile.CopyTo(file);
                }
            }
            else
            {
                using (ZipArchive file = ZipFile.Open(outputFileName.FullName, ZipArchiveMode.Create))
                {
                    var entry = file.CreateEntry(outputFileName.Name + ".dll", CompressionLevel.Optimal);
                    using (Stream archiveStream = entry.Open())
                    {
                        peFile.CopyTo(archiveStream);
                    }
                }
            }

            Program.PrintMessage($"Generated {outputFileName.FullName}");

            if (validate)
                return ValidateMIbcData(tsc, outputFileName, peFile.ToArray(), methodsToAttemptToPlaceIntoProfileData);
            else
                return 0;
        }

        static int ValidateMIbcData(TypeSystemContext tsc, FileInfo outputFileName, byte[] moduleBytes, IEnumerable<MethodProfileData> methodsToAttemptToPrepare)
        {
            var peReader = new System.Reflection.PortableExecutable.PEReader(System.Collections.Immutable.ImmutableArray.Create<byte>(moduleBytes));
            var profileData = MIbcProfileParser.ParseMIbcFile(tsc, peReader, null, null);
            Dictionary<MethodDesc, MethodProfileData> mibcDict = new Dictionary<MethodDesc, MethodProfileData>();

            foreach (var mibcData in profileData.GetAllMethodProfileData())
            {
                mibcDict.Add((MethodDesc)(object)mibcData.Method, mibcData);
            }

            bool failure = false;
            if (methodsToAttemptToPrepare.Count() != mibcDict.Count)
            {
                Program.PrintError($"Not same count of methods {methodsToAttemptToPrepare.Count()} != {mibcDict.Count}");
                failure = true;
            }

            foreach (var entry in methodsToAttemptToPrepare)
            {
                MethodDesc method = entry.Method;
                if (!mibcDict.ContainsKey(method))
                {
                    Program.PrintError($"{method} not found in mibcEntryData");
                    failure = true;
                    continue;
                }
            }

            if (failure)
            {
                return -1;
            }
            else
            {
                Program.PrintMessage($"Validated {outputFileName.FullName}");
                return 0;
            }
        }

    }
}
