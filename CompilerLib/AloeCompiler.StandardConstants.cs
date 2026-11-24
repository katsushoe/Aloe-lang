using System.Reflection;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    /// <summary>Decision 0060: logical source path for __FILE__ (relative, '/' separators). Defaults to &lt;stdin&gt;.</summary>
    public string SourcePath { get; set; } = "<stdin>";

    /// <summary>Decision 0060: value of ALOE_DEBUG for this compilation.</summary>
    public bool DebugBuild { get; set; }

    private sealed partial class Parser
    {
        public string SourcePathValue { get; init; } = "<stdin>";
        public bool DebugBuildValue { get; init; }
        private string _sourcePath => SourcePathValue;
        private bool _debugBuild => DebugBuildValue;

        private static readonly HashSet<string> RuntimeConstantNames = new(StringComparer.Ordinal)
        {
            "ALOE_OS", "ALOE_TARGET", "ALOE_BINDIR", "ALOE_PLUGINDIR", "ALOE_DIR_SEPARATOR", "ALOE_MAX_PATHLEN"
        };

        internal static bool IsReservedStandardName(string name)
            => name.StartsWith("ALOE_", StringComparison.Ordinal) ||
               name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);

        private static string CompilerVersion()
        {
            var version = typeof(AloeCompiler).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }

        /// <summary>Resolves a standard global or magic constant at the current identifier token.</summary>
        private bool TryParseStandardConstant(out ExprType type)
        {
            type = ExprType.Void;
            var token = Current;
            if (!IsReservedStandardName(token.Lexeme))
                return false;
            var name = token.Lexeme;
            if (RuntimeConstantNames.Contains(name))
            {
                Advance();
                Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(name)));
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.RuntimeConstant);
                _lastExpressionClassName = null;
                type = name == "ALOE_MAX_PATHLEN" ? ExprType.Int : ExprType.String;
                return true;
            }

            var version = CompilerVersion().Split('.');
            AloeValue value;
            switch (name)
            {
                case "ALOE_VERSION": value = AloeValue.FromString(CompilerVersion()); type = ExprType.String; break;
                case "ALOE_MAJOR_VERSION": value = AloeValue.FromInt(int.Parse(version[0])); type = ExprType.Int; break;
                case "ALOE_MINOR_VERSION": value = AloeValue.FromInt(int.Parse(version[1])); type = ExprType.Int; break;
                case "ALOE_RELEASE_VERSION": value = AloeValue.FromInt(int.Parse(version[2])); type = ExprType.Int; break;
                case "ALOE_DEBUG": value = AloeValue.FromBool(_debugBuild); type = ExprType.Bool; break;
                case "ALOE_INT_MIN": value = AloeValue.FromInt(int.MinValue); type = ExprType.Int; break;
                case "ALOE_INT_MAX": value = AloeValue.FromInt(int.MaxValue); type = ExprType.Int; break;
                case "ALOE_FLOAT_MIN": value = AloeValue.FromFloat(1.17549435E-38f); type = ExprType.Float; break;
                case "ALOE_FLOAT_MAX": value = AloeValue.FromFloat(float.MaxValue); type = ExprType.Float; break;
                case "ALOE_EOL": value = AloeValue.FromString("\n"); type = ExprType.String; break;
                case "__FILE__": value = AloeValue.FromString(_sourcePath); type = ExprType.String; break;
                case "__DIR__":
                    var slash = _sourcePath.LastIndexOf('/');
                    value = AloeValue.FromString(slash > 0 ? _sourcePath[..slash] : ".");
                    type = ExprType.String;
                    break;
                case "__LINE__": value = AloeValue.FromInt(token.Line); type = ExprType.Int; break;
                case "__FUNCTION__":
                {
                    var function = _currentFunction ?? throw Error(token, "__FUNCTION__ is valid only inside a function or method.");
                    var dot = function.Name.LastIndexOf('.');
                    value = AloeValue.FromString(dot >= 0 ? function.Name[(dot + 1)..] : function.Name);
                    type = ExprType.String;
                    break;
                }
                case "__METHOD__":
                {
                    var function = _currentFunction ?? throw Error(token, "__METHOD__ is valid only inside a function or method.");
                    value = AloeValue.FromString(function.Name);
                    type = ExprType.String;
                    break;
                }
                case "__CLASS__":
                {
                    var owner = _currentFunction?.DeclaringClass ?? _currentFunction?.DeclaringStruct
                        ?? throw Error(token, "__CLASS__ is valid only inside a class or struct declaration.");
                    value = AloeValue.FromString(owner);
                    type = ExprType.String;
                    break;
                }
                case "__TRAIT__":
                {
                    var trait = _currentFunction?.DeclaringTrait
                        ?? throw Error(token, "__TRAIT__ is valid only inside a trait method.");
                    value = AloeValue.FromString(trait);
                    type = ExprType.String;
                    break;
                }
                default:
                    throw Error(token, $"Unknown standard constant '{name}'. Names starting with ALOE_ and __name__ are reserved.");
            }
            Advance();
            Emit(EnumOpcode.PushConst, AddConstant(value));
            _lastExpressionClassName = null;
            return true;
        }
    }
}
