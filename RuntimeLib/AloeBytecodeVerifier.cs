using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using System;
using System.Collections.Generic;

namespace Aloe.RuntimeLib
{
    /// <summary>
    /// Performs load-time structural validation for the current AloeBC prototype.
    /// Type-stack validation is intentionally deferred until FunctionInfo carries
    /// parameter/return/local type metadata.
    /// </summary>
    public static class AloeBytecodeVerifier
    {
        private sealed class ControlState
        {
            public ControlState(EnumOpcode opener)
            {
                Opener = opener;
            }

            public EnumOpcode Opener { get; }
            public bool ElseSeen { get; set; }
        }

        public static void Verify(Module module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));

            if (module.Functions.Count == 0)
                throw Error(-1, "Module contains no functions.");

            if ((uint)module.EntryPointIndex >= (uint)module.Functions.Count)
                throw Error(-1, $"EntryPointIndex is out of range: {module.EntryPointIndex}.");

            VerifyFunctionTable(module);

            for (var functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
                VerifyFunction(module, functionIndex);
        }

        private static void VerifyFunctionTable(Module module)
        {
            var previousEntry = -1;

            for (var i = 0; i < module.Functions.Count; i++)
            {
                var function = module.Functions[i]
                    ?? throw Error(-1, $"Function table entry {i} is null.");

                if ((uint)function.EntryIp >= (uint)module.Code.Count)
                    throw Error(-1, $"Function {i} ('{function.Name}') entry IP {function.EntryIp} is out of range.");

                if (function.EntryIp <= previousEntry)
                    throw Error(-1, "Function entry IPs must be strictly increasing.");

                if (function.ParameterCount > function.LocalCount)
                    throw Error(function.EntryIp,
                        $"Function {i} ('{function.Name}') has ParameterCount={function.ParameterCount} greater than LocalCount={function.LocalCount}.");

                previousEntry = function.EntryIp;
            }
        }

        private static void VerifyFunction(Module module, int functionIndex)
        {
            var function = module.Functions[functionIndex];
            var start = function.EntryIp;
            var end = functionIndex + 1 < module.Functions.Count
                ? module.Functions[functionIndex + 1].EntryIp
                : module.Code.Count;

            var controls = new Stack<ControlState>();
            var exceptionRegions = new Stack<int>();
            var catches = new Stack<int>();
            var catchTargets = new HashSet<int>();
            var catchTableStarts = new HashSet<int>();
            var catchDescriptorIps = new HashSet<int>();

            for (var ip = start; ip < end; ip++)
            {
                var instruction = module.Code[ip];
                if (instruction.Opcode != EnumOpcode.TryBegin ||
                    instruction.Operand0 <= ip || instruction.Operand0 >= end ||
                    module.Code[instruction.Operand0].Opcode != EnumOpcode.TryCatch)
                    continue;

                var tableStart = instruction.Operand0;
                if (!catchTableStarts.Add(tableStart))
                    throw Error(ip, $"Catch table {tableStart} is shared by multiple TRY_BEGIN instructions.");

                var nestedTryDepth = 0;
                var matchingTryEnd = -1;
                for (var protectedIp = ip + 1; protectedIp < tableStart; protectedIp++)
                {
                    if (module.Code[protectedIp].Opcode == EnumOpcode.TryBegin)
                    {
                        nestedTryDepth++;
                    }
                    else if (module.Code[protectedIp].Opcode == EnumOpcode.TryEnd)
                    {
                        if (nestedTryDepth == 0)
                        {
                            matchingTryEnd = protectedIp;
                            break;
                        }
                        nestedTryDepth--;
                    }
                }
                if (matchingTryEnd < 0)
                    throw Error(ip, "TRY_BEGIN catch table has no matching TRY_END before it.");

                var catchAllSeen = false;
                var descriptorCount = 0;
                for (var descriptorIp = tableStart;
                     descriptorIp < end && module.Code[descriptorIp].Opcode == EnumOpcode.TryCatch;
                     descriptorIp++)
                {
                    descriptorCount++;
                    catchDescriptorIps.Add(descriptorIp);
                    var descriptor = module.Code[descriptorIp];
                    if (descriptor.Operand0 <= matchingTryEnd || descriptor.Operand0 >= tableStart)
                        throw Error(descriptorIp, "TRY_CATCH target must be after its matching TRY_END and before the catch table.");
                    if (!catchTargets.Add(descriptor.Operand0))
                        throw Error(descriptorIp, $"Catch target {descriptor.Operand0} is shared by multiple clauses.");
                    if (descriptor.Operand1 == 0)
                    {
                        if (catchAllSeen)
                            throw Error(descriptorIp, "A catch-all clause must be the final clause.");
                        catchAllSeen = true;
                        continue;
                    }

                    if (catchAllSeen)
                        throw Error(descriptorIp, "A catch clause follows a catch-all clause.");
                    var constantIndex = descriptor.Operand1 - 1;
                    if ((uint)constantIndex >= (uint)module.Constants.Count ||
                        module.Constants[constantIndex].Kind != EnumValueKind.String ||
                        !AloeExceptionTypes.TryResolve(module.Constants[constantIndex].AsString, out _))
                        throw Error(descriptorIp, "TRY_CATCH has an invalid exception type descriptor.");
                }

                if (descriptorCount == 0)
                    throw Error(ip, "TRY_BEGIN catch table is empty.");
            }

            for (var ip = start; ip < end; ip++)
            {
                if (catchTargets.Remove(ip))
                    catches.Push(ip);

                var instruction = module.Code[ip];
                var opcode = instruction.Opcode;

                if (!Enum.IsDefined(typeof(EnumOpcode), opcode))
                    throw Error(ip, $"Unknown opcode value 0x{(ushort)opcode:X4}.");

                switch (opcode)
                {
                    case EnumOpcode.Nop:
                    case EnumOpcode.Add:
                    case EnumOpcode.Sub:
                    case EnumOpcode.Mul:
                    case EnumOpcode.Div:
                    case EnumOpcode.Mod:
                    case EnumOpcode.BitAnd:
                    case EnumOpcode.BitOr:
                    case EnumOpcode.BitXor:
                    case EnumOpcode.BitNot:
                    case EnumOpcode.BitShiftLeft:
                    case EnumOpcode.BitShiftRight:
                    case EnumOpcode.CmpEq:
                    case EnumOpcode.CmpNe:
                    case EnumOpcode.CmpLt:
                    case EnumOpcode.CmpLe:
                    case EnumOpcode.CmpGt:
                    case EnumOpcode.CmpGe:
                    case EnumOpcode.Return:
                    case EnumOpcode.Halt:
                        break;

                    case EnumOpcode.TryBegin:
                        if (instruction.Operand0 <= ip || instruction.Operand0 >= end ||
                            instruction.Operand1 < 0 || instruction.Operand1 > module.Constants.Count)
                            throw Error(ip, "TRY_BEGIN requires a forward catch target within the same function.");
                        if (catchTableStarts.Contains(instruction.Operand0))
                        {
                            if (instruction.Operand1 != 0)
                                throw Error(ip, "TRY_BEGIN with a catch table must not carry a legacy exception type.");
                        }
                        else
                        {
                            if (!catchTargets.Add(instruction.Operand0))
                                throw Error(ip, $"Catch target {instruction.Operand0} is shared by multiple TRY_BEGIN instructions.");
                            if (instruction.Operand1 > 0)
                            {
                                var constantIndex = instruction.Operand1 - 1;
                                if ((uint)constantIndex >= (uint)module.Constants.Count ||
                                    module.Constants[constantIndex].Kind != EnumValueKind.String ||
                                    !AloeExceptionTypes.TryResolve(module.Constants[constantIndex].AsString, out _))
                                    throw Error(ip, "TRY_BEGIN has an invalid exception type descriptor.");
                            }
                        }
                        exceptionRegions.Push(instruction.Operand0);
                        break;

                    case EnumOpcode.TryCatch:
                        if (!catchDescriptorIps.Contains(ip))
                            throw Error(ip, "TRY_CATCH is outside a catch table.");
                        break;

                    case EnumOpcode.TryEnd:
                        if (exceptionRegions.Count == 0)
                            throw Error(ip, "TRY_END has no matching TRY_BEGIN.");
                        exceptionRegions.Pop();
                        break;

                    case EnumOpcode.CatchEnd:
                        if (catches.Count == 0)
                            throw Error(ip, "CATCH_END is outside a catch region.");
                        if (instruction.Operand0 == 0)
                        {
                            catches.Pop();
                        }
                        else if (instruction.Operand0 == 1)
                        {
                            // Transfer cleanup does not close the lexical source catch.
                            // Other branches still reach its ordinary CATCH_END.
                            if (ip > start && module.Code[ip - 1].Opcode == EnumOpcode.CatchEnd &&
                                module.Code[ip - 1].Operand0 == 1)
                                break;
                            var cleanupEnd = ip;
                            while (cleanupEnd < end && module.Code[cleanupEnd].Opcode == EnumOpcode.CatchEnd &&
                                   module.Code[cleanupEnd].Operand0 == 1)
                                cleanupEnd++;
                            if (cleanupEnd - ip > catches.Count || cleanupEnd >= end ||
                                module.Code[cleanupEnd].Opcode != EnumOpcode.Br)
                                throw Error(ip, "CATCH_END transfer cleanup requires an immediate branch and sufficient catch depth.");
                        }
                        else
                        {
                            throw Error(ip, "CATCH_END has an invalid cleanup mode.");
                        }
                        break;

                    case EnumOpcode.Rethrow:
                        if (catches.Count == 0)
                            throw Error(ip, "RETHROW is outside a catch region.");
                        break;

                    case EnumOpcode.PushConst:
                        RequireIndex(ip, "constant", instruction.Operand0, module.Constants.Count);
                        break;

                    case EnumOpcode.StructCreate:
                    case EnumOpcode.StructGet:
                    case EnumOpcode.StructSet:
                        if (instruction.Operand0 < 0)
                            throw Error(ip, "Struct field count/index must be non-negative.");
                        break;

                    case EnumOpcode.LoadLocal:
                    case EnumOpcode.StoreLocal:
                        RequireIndex(ip, "local", instruction.Operand0, function.LocalCount);
                        break;

                    case EnumOpcode.Call:
                        RequireIndex(ip, "function", instruction.Operand0, module.Functions.Count);
                        break;

                    case EnumOpcode.Syscall:
                        if (instruction.Operand0 < 0)
                            throw Error(ip, $"Syscall id must be non-negative; found {instruction.Operand0}.");
                        break;

                    case EnumOpcode.Block:
                    case EnumOpcode.Loop:
                    case EnumOpcode.If:
                        controls.Push(new ControlState(opcode));
                        break;

                    case EnumOpcode.Else:
                        if (controls.Count == 0 || controls.Peek().Opener != EnumOpcode.If)
                            throw Error(ip, "ELSE does not match an active IF.");
                        if (controls.Peek().ElseSeen)
                            throw Error(ip, "IF contains more than one ELSE.");
                        controls.Peek().ElseSeen = true;
                        break;

                    case EnumOpcode.End:
                        if (controls.Count == 0)
                            throw Error(ip, "END has no matching BLOCK/LOOP/IF.");
                        controls.Pop();
                        break;

                    case EnumOpcode.Br:
                    case EnumOpcode.BrIf:
                        if (instruction.Operand0 < 0 || instruction.Operand0 >= controls.Count)
                            throw Error(ip,
                                $"Invalid branch depth {instruction.Operand0}; active labels={controls.Count}.");
                        break;

                    default:
                        throw Error(ip, $"Opcode {opcode} is not supported by the verifier.");
                }
            }

            if (controls.Count != 0)
                throw Error(end - 1,
                    $"Function {functionIndex} ('{function.Name}') ends with {controls.Count} unclosed structured-control block(s).");
            if (exceptionRegions.Count != 0)
                throw Error(end - 1, $"Function {functionIndex} ('{function.Name}') ends with an unclosed TRY_BEGIN region.");
            if (catches.Count != 0 || catchTargets.Count != 0)
                throw Error(end - 1, $"Function {functionIndex} ('{function.Name}') contains an incomplete catch region.");
        }

        private static void RequireIndex(int ip, string kind, int index, int count)
        {
            if ((uint)index >= (uint)count)
                throw Error(ip, $"Invalid {kind} index {index}; count={count}.");
        }

        private static VmException Error(int ip, string message)
            => new(ip >= 0 ? $"AloeBC verification failed at IP {ip}: {message}" : $"AloeBC verification failed: {message}");
    }
}
