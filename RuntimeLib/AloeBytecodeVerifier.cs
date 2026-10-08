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

            for (var ip = start; ip < end; ip++)
            {
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
                    case EnumOpcode.CmpEq:
                    case EnumOpcode.CmpNe:
                    case EnumOpcode.CmpLt:
                    case EnumOpcode.CmpLe:
                    case EnumOpcode.CmpGt:
                    case EnumOpcode.CmpGe:
                    case EnumOpcode.Return:
                    case EnumOpcode.Halt:
                        break;

                    case EnumOpcode.PushConst:
                        RequireIndex(ip, "constant", instruction.Operand0, module.Constants.Count);
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
