// Copyright (C) 2015-2026 The Neo Project.
//
// SmartContract.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Neo.Extensions;
using Neo.SmartContract.Testing.Extensions;
using Neo.SmartContract.Testing.Storage;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Neo.SmartContract.Testing
{
    public class SmartContract : IDisposable
    {
        internal readonly TestEngine Engine;
        private readonly Type _contractType;
        private readonly Dictionary<string, FieldInfo?> _notifyCache = new();

        public event TestEngine.OnRuntimeLogDelegate? OnRuntimeLog;

        /// <summary>
        /// Contract hash
        /// </summary>
        public UInt160 Hash { get; }

        /// <summary>
        /// Storage for this contract
        /// </summary>
        public SmartContractStorage Storage { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="initialize">Initialize object</param>
        protected SmartContract(SmartContractInitialize initialize)
        {
            Engine = initialize.Engine;
            Hash = initialize.Hash;
            Storage = new SmartContractStorage(this, initialize.ContractId);
            _contractType = GetType().BaseType ?? GetType(); // Mock
        }

        /// <summary>
        /// Invoke to NeoVM
        /// </summary>
        /// <param name="methodName">Method name</param>
        /// <param name="args">Arguments</param>
        /// <returns>Object</returns>
        internal StackItem Invoke(string methodName, params object[] args)
        {
            // Compose script

            using ScriptBuilder script = new();

            if (RequiresDirectArgumentInjection(args))
            {
                script.EmitSysCall(ApplicationEngine.System_Contract_Call);
                return Engine.Execute(script.ToArray(), beforeExecute: engine =>
                {
                    var context = engine.CurrentContext ?? throw new InvalidOperationException("Execution context is not initialized.");
                    context.EvaluationStack.Push(CreateArgumentArray(engine, args));
                    context.EvaluationStack.Push(engine.Convert(Engine.CallFlags));
                    context.EvaluationStack.Push(engine.Convert(methodName));
                    context.EvaluationStack.Push(engine.Convert(Hash));
                });
            }

            ConvertArgs(script, args);
            script.EmitPush(Engine.CallFlags);
            script.EmitPush(methodName);
            script.EmitPush(Hash);
            script.EmitSysCall(ApplicationEngine.System_Contract_Call);

            // Execute

            return Engine.Execute(script.ToArray());
        }

        private static bool RequiresDirectArgumentInjection(object[]? args)
        {
            if (args is null) return false;

            foreach (var arg in args)
            {
                if (arg is InteropInterface)
                    return true;
                if (arg is Action<ApplicationEngine>)
                    return true;
                if (arg is object[] nested && RequiresDirectArgumentInjection(nested))
                    return true;
                if (arg is IEnumerable<object> enumerable && RequiresDirectArgumentInjection(enumerable.ToArray()))
                    return true;
            }

            return false;
        }

        private static Neo.VM.Types.Array CreateArgumentArray(ApplicationEngine engine, object[] args)
        {
            var items = new StackItem[args.Length];
            for (var index = 0; index < args.Length; index++)
                items[index] = ConvertArgToStackItem(engine, args[index]);
            return new Neo.VM.Types.Array(engine.ReferenceCounter, items);
        }

        private static StackItem ConvertArgToStackItem(ApplicationEngine engine, object? arg)
        {
            if (arg is object[] nestedArray)
                return CreateArgumentArray(engine, nestedArray);
            if (arg is IEnumerable<object> enumerable)
                return CreateArgumentArray(engine, enumerable.ToArray());

            if (ReferenceEquals(arg, InvalidTypes.InvalidUInt160.InvalidLength) ||
                ReferenceEquals(arg, InvalidTypes.InvalidUInt256.InvalidLength) ||
                ReferenceEquals(arg, InvalidTypes.InvalidECPoint.InvalidLength))
            {
                arg = System.Array.Empty<byte>();
            }
            else if (ReferenceEquals(arg, InvalidTypes.InvalidUInt160.InvalidType) ||
                ReferenceEquals(arg, InvalidTypes.InvalidUInt256.InvalidType))
            {
                arg = BigInteger.Zero;
            }
            else if (ReferenceEquals(arg, InvalidTypes.InvalidECPoint.InvalidType))
            {
                arg = System.Array.Empty<byte>();
            }
            else if (arg is PrimitiveType primitive)
            {
                arg = primitive switch
                {
                    ByteString byteString => byteString.GetSpan().ToArray(),
                    VM.Types.Boolean boolean => boolean.GetBoolean(),
                    VM.Types.Integer integer => integer.GetInteger(),
                    _ => primitive
                };
            }
            else if (arg is Action<ApplicationEngine> onItem)
            {
                var context = engine.CurrentContext ?? throw new InvalidOperationException("Execution context is not initialized.");
                var stackSize = context.EvaluationStack.Count;
                onItem(engine);
                if (context.EvaluationStack.Count != stackSize + 1)
                    throw new InvalidOperationException(
                        $"Action<ApplicationEngine> argument {onItem.Method.Name} must push exactly one stack item.");
                return context.EvaluationStack.Pop();
            }

            return engine.Convert(arg);
        }

        private static void ConvertArgs(ScriptBuilder script, object[] args)
        {
            if (args is null || args.Length == 0)
                script.Emit(OpCode.NEWARRAY0);
            else
            {
                for (int i = args.Length - 1; i >= 0; i--)
                {
                    var arg = args[i];

                    if (arg is object[] arg2)
                    {
                        ConvertArgs(script, arg2);
                        continue;
                    }
                    else if (arg is IEnumerable<object> argEnumerable)
                    {
                        ConvertArgs(script, argEnumerable.ToArray());
                        continue;
                    }

                    if (ReferenceEquals(arg, InvalidTypes.InvalidUInt160.InvalidLength) ||
                        ReferenceEquals(arg, InvalidTypes.InvalidUInt256.InvalidLength) ||
                        ReferenceEquals(arg, InvalidTypes.InvalidECPoint.InvalidLength))
                    {
                        arg = System.Array.Empty<byte>();
                    }
                    else if (ReferenceEquals(arg, InvalidTypes.InvalidUInt160.InvalidType) ||
                        ReferenceEquals(arg, InvalidTypes.InvalidUInt256.InvalidType))
                    {
                        arg = BigInteger.Zero;
                    }
                    else if (ReferenceEquals(arg, InvalidTypes.InvalidECPoint.InvalidType))
                    {
                        arg = System.Array.Empty<byte>();
                    }
                    else if (arg is InteropInterface interop)
                    {
                        throw new NotSupportedException(
                            $"InteropInterface arguments are no longer supported in the RISC-V-only test runtime: {interop.GetType().Name}.");
                    }
                    else if (arg is Action<ApplicationEngine> onItem)
                    {
                        throw new NotSupportedException(
                            $"Action<ApplicationEngine> dynamic argument injection is no longer supported in the RISC-V-only test runtime: {onItem.Method.Name}.");
                    }
                    else if (arg is PrimitiveType)
                    {
                        if (arg is ByteString vmbs)
                        {
                            arg = vmbs.GetSpan().ToArray();
                        }
                        else if (arg is VM.Types.Boolean vmb)
                        {
                            arg = vmb.GetBoolean();
                        }
                        else if (arg is VM.Types.Integer vmi)
                        {
                            arg = vmi.GetInteger();
                        }
                    }

                    script.EmitPush(arg);
                }
                script.EmitPush(args.Length);
                script.Emit(OpCode.PACK);
            }
        }

        /// <summary>
        /// Invoke OnRuntimeLog
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="message">Message</param>
        internal void InvokeOnRuntimeLog(UInt160 sender, string message)
        {
            OnRuntimeLog?.Invoke(sender, message);
        }

        /// <summary>
        /// Invoke on notify
        /// </summary>
        /// <param name="eventName">Event name</param>
        /// <param name="state">State</param>
        internal void InvokeOnNotify(string eventName, VM.Types.Array state)
        {
            if (!_notifyCache.TryGetValue(eventName, out var evField))
            {
                var ev = _contractType.GetEvent(eventName);
                if (ev is null)
                {
                    ev = _contractType.GetEvents()
                        .FirstOrDefault(u => u.Name == eventName || u.GetCustomAttribute<DisplayNameAttribute>(true)?.DisplayName == eventName);

                    if (ev is null)
                    {
                        _notifyCache[eventName] = null;
                        return;
                    }
                }

                _notifyCache[eventName] = evField = _contractType.GetField(ev.Name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);
            }

            // Not found
            if (evField is null) return;
            if (evField.GetValue(this) is not Delegate del) return;

            // Avoid parse if is not needed

            var invocations = del.GetInvocationList();
            if (invocations.Length == 0) return;

            // Invoke

            var args = state.ConvertTo(del.Method.GetParameters(), Engine.StringInterpreter);

            foreach (var handler in invocations)
            {
                handler.Method.Invoke(handler.Target, args);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator UInt160(SmartContract value) => value.Hash;

        /// <summary>
        /// Release mock
        /// </summary>
        public void Dispose()
        {
            Engine.ReleaseMock(this);
        }
    }
}
