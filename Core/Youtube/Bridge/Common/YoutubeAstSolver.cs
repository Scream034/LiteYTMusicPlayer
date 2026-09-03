using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Обеспечивает высокопроизводительный разбор и модификацию плеера на основе AST Acornima.
/// Использует лексический анализатор областей видимости для достижения максимального сжатия JS кода (до 10-15 КБ).
/// </summary>
public static partial class YoutubeAstSolver
{
    private static readonly MatchTemplate IdentifierTemplate = new()
    {
        Or = [
            new()
            {
                NodeType = typeof(ExpressionStatement),
                Expression = new()
                {
                    NodeType = typeof(AssignmentExpression),
                    Operator = "=",
                    Left = new()
                    {
                        Or = [
                            new() { NodeType = typeof(Identifier) },
                            new() { NodeType = typeof(MemberExpression) }
                        ]
                    },
                    Right = new()
                    {
                        NodeType = typeof(FunctionExpression),
                        Async = false
                    }
                }
            },
            new()
            {
                NodeType = typeof(FunctionDeclaration),
                Async = false,
                Id = new() { NodeType = typeof(Identifier) }
            },
            new()
            {
                NodeType = typeof(VariableDeclaration),
                AnyKey = [
                    new()
                    {
                        NodeType = typeof(VariableDeclarator),
                        Init = new()
                        {
                            NodeType = typeof(FunctionExpression),
                            Async = false
                        }
                    }
                ]
            }
        ]
    };

    private static readonly MatchTemplate AsdasdTemplate = new()
    {
        NodeType = typeof(ExpressionStatement),
        Expression = new()
        {
            NodeType = typeof(CallExpression),
            Callee = new()
            {
                NodeType = typeof(MemberExpression),
                Object = new() { NodeType = typeof(Identifier) },
                Optional = false
            },
            Arguments = [
                new() { NodeType = typeof(Literal), Value = "alr" },
                new() { NodeType = typeof(Literal), Value = "yes" }
            ],
            Optional = false
        }
    };

    private const string SetupScript = """
    if (typeof globalThis.__log === "undefined") {
        globalThis.__log = function() {};
    }
    globalThis._result = globalThis._result || {};

    globalThis._yt_player = globalThis._yt_player || {};
    (function() {
        var stsVal = typeof globalThis.__sts !== "undefined" ? globalThis.__sts : 20590;
        globalThis._yt_player.sts = stsVal;
        globalThis._yt_player.qj = stsVal;
        globalThis._yt_player.Mo = stsVal;
        globalThis._yt_player.signatureTimestamp = stsVal;
    })();

    if (typeof globalThis.g === "undefined") {
        try {
            var _gFallback = function() {}; 
            _gFallback.prototype = {};
            Object.defineProperty(globalThis, 'g', {
                value: new Proxy(globalThis._yt_player, {
                    get: function(t, p) {
                        if (p === 'then') return void 0;
                        if (typeof p === 'symbol') return void 0;
                        if (p in t) return t[p];
                        return _gFallback;
                    },
                    set: function(t, p, v) { t[p] = v; return true; },
                    has: function() { return true; }
                }),
                writable: true,
                configurable: true,
                enumerable: true
            });
        } catch (e) {
            globalThis.g = globalThis._yt_player;
        }
    }

    (function() {
        var URLPolyfill = function(url, base) {
            var absoluteUrl = url;
            if (base) {
                if (url.startsWith("/")) {
                    var originMatch = base.match(/^https?:\/\/[^\/]+/);
                    absoluteUrl = (originMatch ? originMatch[0] : "") + url;
                } else if (!url.startsWith("http")) {
                    absoluteUrl = base.replace(/\/?[^\/]*$/, "/") + url;
                }
            }
            
            var match = absoluteUrl.match(/^(https?:)\/\/([^\/?#]+)([^?#]*)(?:\?([^#]*))?(?:#(.*))?$/);
            if (!match) throw new Error("Invalid URL: " + url);
            
            this.protocol = match[1];
            this.host = match[2];
            this.hostname = match[2].split(":")[0];
            this.pathname = match[3] || "/";
            this.search = match[4] ? "?" + match[4] : "";
            this.hash = match[5] ? "#" + match[5] : "";
            this.href = absoluteUrl;
            this.origin = this.protocol + "//" + this.host;
        };
        URLPolyfill.prototype.toString = function() { return this.href; };

        function defineGlobal(name, val) {
            try {
                Object.defineProperty(globalThis, name, {
                    value: val,
                    writable: true,
                    configurable: true,
                    enumerable: true
                });
            } catch(e) {
                globalThis[name] = val;
            }
        }

        defineGlobal('URL', URLPolyfill);
        defineGlobal('location', new URLPolyfill("https://www.youtube.com/watch?v=bMD38TVNUF8"));
        defineGlobal('self', globalThis);
        defineGlobal('window', globalThis);
        defineGlobal('top', globalThis);
        defineGlobal('parent', globalThis);
        defineGlobal('document', Object.create(null));
        defineGlobal('navigator', Object.create(null));
    })();

    if (typeof globalThis.XMLHttpRequest === "undefined") {
        globalThis.XMLHttpRequest = { prototype: {} };
    }

    globalThis.console = {
        log: function() {
            var msg = Array.prototype.slice.call(arguments).join(' ');
            globalThis.__log("[JS console.log] " + msg);
        },
        error: function() {
            var msg = Array.prototype.slice.call(arguments).join(' ');
            globalThis.__log("[JS console.error] " + msg);
        }
    };
    """;

    /// <summary>
    /// Парсит и препроцессит JS плеер, вырезая все неиспользуемые компоненты с помощью Scope-Aware Tree Shaking.
    /// Снижает результирующий размер кода с 2.5 МБ до ~10-15 КБ.
    /// </summary>
    /// <param name="baseJs">Полный исходный код base.js.</param>
    /// <returns>Препроцессированный и оптимизированный код JS.</returns>
    public static string PreprocessPlayer(string baseJs)
    {
        Log.Debug($"[AstSolver] Preprocessing player script. Length: {baseJs.Length / 1024} KB");

        var stsMatch = StsRegex().Match(baseJs);
        string sts = stsMatch.Success ? stsMatch.Groups[1].Value : "1337";
        Log.Debug($"[AstSolver] Extracted Signature Timestamp (STS): {sts}");

        // В Acornima 1.1+ и 1.6.2+ тяжелая компиляция регулярных выражений .NET по умолчанию
        // отключена, если не задан колбэк OnRegExp. Это исключает лишние аллокации в SOH,
        // ускоряет парсинг в два раза и избавляет от предупреждений компилятора CS0618.
        var parser = new Parser(new ParserOptions
        {
            Tolerant = false
        });
        var program = parser.ParseScript(baseJs);

        var body = program.Body;
        Node? iifeNode = null;
        FunctionExpression? iifeFunc = null;

        if (body.Count == 1)
        {
            var func = body[0];
            if (func is ExpressionStatement es && es.Expression is CallExpression ce)
            {
                var callee = ce.Callee;
                while (callee is ParenthesizedExpression pe)
                {
                    callee = pe.Expression;
                }
                if (callee is FunctionExpression fe && fe.Body is not null)
                {
                    iifeNode = func;
                    iifeFunc = fe;
                }
            }
        }
        else if (body.Count == 2)
        {
            var func = body[1];
            if (func is ExpressionStatement es && es.Expression is CallExpression ce)
            {
                var callee = ce.Callee;
                while (callee is ParenthesizedExpression pe)
                {
                    callee = pe.Expression;
                }
                if (callee is FunctionExpression fe && fe.Body is not null)
                {
                    iifeNode = func;
                    iifeFunc = fe;
                }
            }
        }

        if (iifeNode is null || iifeFunc is null)
        {
            throw new InvalidOperationException("Unexpected player structure: Failed to locate main IIFE wrapper.");
        }

        var targetStatements = iifeFunc.Body.Body;
        var plainStatements = new List<Statement>(targetStatements.Count);

        for (int i = 0; i < targetStatements.Count; i++)
        {
            var node = targetStatements[i];

            if (i == 0 && node is VariableDeclaration vd && vd.Declarations.Count == 1 &&
                vd.Declarations[0].Id is Identifier id && id.Name == "window")
            {
                continue;
            }

            if (node is ExpressionStatement exprEs)
            {
                if (exprEs.Expression is AssignmentExpression or Literal)
                {
                    plainStatements.Add(node);
                }
            }
            else
            {
                plainStatements.Add(node);
            }
        }

        // --- SCOPE-AWARE TREE SHAKING ---
        var stmtToDeclared = new Dictionary<Statement, HashSet<string>>();
        var declaredToStmt = new Dictionary<string, List<Statement>>(StringComparer.Ordinal);

        foreach (var stmt in plainStatements)
        {
            var declared = new HashSet<string>(32, StringComparer.Ordinal);
            CollectDeclaredIdentifiers(stmt, declared);
            stmtToDeclared[stmt] = declared;

            foreach (var name in declared)
            {
                if (!declaredToStmt.TryGetValue(name, out var list))
                {
                    list = [];
                    declaredToStmt[name] = list;
                }
                list.Add(stmt);
            }
        }

        // #if DEBUG
        //         // Временно
        //         var hubs = declaredToStmt
        //             .Where(kvp => kvp.Value.Count > 2)
        //             .OrderByDescending(kvp => kvp.Value.Count);

        //         foreach (var hub in hubs)
        //             Log.Warn($"[AstSolver] Hub-node: '{hub.Key}' → {hub.Value.Count} statements");
        // #endif

        var requiredIdentifiers = new HashSet<string>(32, StringComparer.Ordinal);
        var solverStatements = new List<Statement>();

        for (int i = 0; i < plainStatements.Count; i++)
        {
            var stmt = plainStatements[i];
            var solver = Extract(stmt, baseJs);
            if (solver is not null)
            {
                solverStatements.Add(stmt);
                if (stmtToDeclared.TryGetValue(stmt, out var declared))
                {
                    foreach (var name in declared)
                    {
                        requiredIdentifiers.Add(name);
                    }
                }
            }
        }

        List<Statement> shakenStatements;
        if (solverStatements.Count > 0)
        {
            var topLevelNames = new HashSet<string>(declaredToStmt.Keys, StringComparer.Ordinal);
            var includedStatements = new HashSet<Statement>();
            var queue = new Queue<string>(requiredIdentifiers);
            var visitedIdentifiers = new HashSet<string>(requiredIdentifiers, StringComparer.Ordinal);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();

                if (declaredToStmt.TryGetValue(id, out var stmts))
                {
                    foreach (var stmt in stmts)
                    {
                        if (includedStatements.Add(stmt))
                        {
                            var collector = new ScopeCollector(topLevelNames);
                            var referenced = collector.Analyze(stmt);

                            foreach (var refId in referenced)
                            {
                                if (visitedIdentifiers.Add(refId))
                                {
                                    queue.Enqueue(refId);
                                }
                            }
                        }
                    }
                }
            }

            shakenStatements = new List<Statement>(includedStatements.Count);
            foreach (var stmt in plainStatements)
            {
                if (includedStatements.Contains(stmt))
                {
                    shakenStatements.Add(stmt);
                }
            }

            Log.Info($"[AstSolver] Tree shaking complete. Reduced statements from {plainStatements.Count} to {shakenStatements.Count}");
        }
        else
        {
            Log.Warn("[AstSolver] No solver statements found! Falling back to full player code.");
            shakenStatements = plainStatements;
        }

        var (nSolvers, sigSolvers) = GetSolutions(shakenStatements, baseJs);

        var sb = new StringBuilder(32 * 1024);

        sb.AppendLine($"globalThis.__sts = {sts};");
        sb.AppendLine(SetupScript);

        if (body.Count == 2)
        {
            var stmt0 = body[0];
            sb.Append(baseJs.AsSpan(stmt0.Start, stmt0.End - stmt0.Start)).AppendLine(";");
        }

        int headerStart = iifeNode.Start;
        int headerEnd = iifeFunc.Body.Start + 1;
        sb.Append(baseJs.AsSpan(headerStart, headerEnd - headerStart)).AppendLine();

        for (int i = 0; i < shakenStatements.Count; i++)
        {
            var stmt = shakenStatements[i];
            int start = stmt.Start;
            int end = stmt.End;
            sb.Append(baseJs.AsSpan(start, end - start));
            sb.AppendLine(";");
        }

        AppendSolverAssignments(sb, "n", nSolvers);
        AppendSolverAssignments(sb, "sig", sigSolvers);

        int footerStart = iifeFunc.Body.End - 1;
        int footerEnd = iifeNode.End;
        sb.Append(baseJs.AsSpan(footerStart, footerEnd - footerStart)).AppendLine();

        var result = sb.ToString();

#if DEBUG
        try
        {
            var debugPath = Path.Combine(G.Folder.Logs, $"player_debug_{sts}.js");
            AtomicFile.WriteText(debugPath, result, createBackup: false);
            Log.Info($"[AstSolver] Preprocessed raw JS written to: {debugPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AstSolver] Failed to write raw JS debug file: {ex.Message}");
        }
#endif

        Log.Debug($"[AstSolver] Preprocessing complete. Final size: {result.Length / 1024} KB. Solvers - N: {nSolvers.Count}, Sig: {sigSolvers.Count}");
        return result;
    }

    /// <summary>
    /// Рекурсивно собирает идентификаторы, объявленные или инициализированные в рамках узла.
    /// Исключает ложный захват используемых переменных из правых частей выражений.
    /// </summary>
    private static void CollectDeclaredIdentifiers(Node? node, HashSet<string> declared)
    {
        if (node is null) return;

        switch (node)
        {
            case VariableDeclaration vd:
                foreach (var decl in vd.Declarations)
                {
                    CollectDeclaredIdentifiers(decl.Id, declared);
                }
                break;
            case VariableDeclarator vdr:
                CollectDeclaredIdentifiers(vdr.Id, declared);
                break;
            case FunctionDeclaration fd:
                if (fd.Id is not null)
                {
                    declared.Add(fd.Id.Name);
                }
                break;
            case ClassDeclaration cd:
                if (cd.Id is not null)
                {
                    declared.Add(cd.Id.Name);
                }
                break;
            case ExpressionStatement es:
                if (es.Expression is AssignmentExpression ae && ae.Operator == Operator.Assignment)
                {
                    // Было: CollectDeclaredIdentifiers(ae.Left, declared);
                    // Это тоже сводило g.abc → "g" через MemberExpression case

                    if (ae.Left is MemberExpression me && !me.Computed)
                    {
                        var path = GetMemberPath(me);
                        if (path is not null)
                        {
                            declared.Add(path);
                            break;
                        }
                    }
                    CollectDeclaredIdentifiers(ae.Left, declared);
                }
                break;
            case AssignmentExpression aee when aee.Operator == Operator.Assignment:
                CollectDeclaredIdentifiers(aee.Left, declared);
                break;
            case MemberExpression me:
                var fullPath = GetMemberPath(me);
                if (fullPath is not null)
                    declared.Add(fullPath);   // "g.abc" вместо "g"
                else
                {
                    // computed access: g[x] — fallback на корень
                    var root = GetRootIdentifier(me);
                    if (root is not null)
                        declared.Add(root);
                }
                break;
            case Identifier id:
                declared.Add(id.Name);
                break;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop is Property p) CollectDeclaredIdentifiers(p.Value, declared);
                    else if (prop is RestElement r) CollectDeclaredIdentifiers(r.Argument, declared);
                }
                break;
            case ArrayPattern ap:
                foreach (var el in ap.Elements)
                {
                    CollectDeclaredIdentifiers(el, declared);
                }
                break;
            case AssignmentPattern asp:
                CollectDeclaredIdentifiers(asp.Left, declared);
                break;
            case RestElement re:
                CollectDeclaredIdentifiers(re.Argument, declared);
                break;
        }
    }

    private static string? GetRootIdentifier(Node? node)
    {
        while (node is MemberExpression me)
        {
            node = me.Object;
        }
        return node is Identifier id ? id.Name : null;
    }

    /// <summary>
    /// Строит полный путь MemberExpression: g.abc → "g.abc", g.k.xyz → "g.k.xyz".
    /// Возвращает null для computed доступа (g[x]) и не-идентификаторов (f().abc).
    /// </summary>
    private static string? GetMemberPath(Node? node)
    {
        if (node is Identifier id) return id.Name;
        if (node is MemberExpression me && !me.Computed && me.Property is Identifier prop)
        {
            var objPath = GetMemberPath(me.Object);
            return objPath is not null ? $"{objPath}.{prop.Name}" : null;
        }
        return null;
    }

    /// <summary>
    /// Лексический Scope-анализатор для точечного исключения локальных переменных.
    /// Предотвращает ложный захват глобальных зависимостей при совпадении коротких имён (a, b, c).
    /// </summary>
    private sealed class ScopeCollector(HashSet<string> topLevelDeclared)
    {
        private readonly HashSet<string> _topLevelDeclared = topLevelDeclared;
        private readonly HashSet<string> _referenced = new(32, StringComparer.Ordinal);
        private readonly List<HashSet<string>> _scopes = [];

        public HashSet<string> Analyze(Node node)
        {
            Visit(node);
            return _referenced;
        }

        private void PushScope() => _scopes.Add(new HashSet<string>(StringComparer.Ordinal));
        private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

        private void DeclareLocal(string name)
        {
            if (_scopes.Count > 0)
            {
                _scopes[^1].Add(name);
            }
        }

        private bool IsLocal(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].Contains(name)) return true;
            }
            return false;
        }

        private void Visit(Node? node)
        {
            if (node is null) return;

            if (node is IFunction func)
            {
                PushScope();
                foreach (var param in func.Params)
                {
                    DeclarePattern(param);
                }
                Visit(func.Body);
                PopScope();
                return;
            }

            if (node is VariableDeclaration vd)
            {
                foreach (var decl in vd.Declarations)
                {
                    Visit(decl.Init);
                    DeclarePattern(decl.Id);
                }
                return;
            }

            if (node is Identifier id)
            {
                var name = id.Name;
                if (_topLevelDeclared.Contains(name) && !IsLocal(name))
                {
                    _referenced.Add(name);
                }
                return;
            }

            if (node is MemberExpression me)
            {
                // Пробуем полный путь: g.abc → "g.abc"
                if (!me.Computed)
                {
                    var fullPath = GetMemberPath(me);
                    if (fullPath is not null && _topLevelDeclared.Contains(fullPath))
                    {
                        _referenced.Add(fullPath);
                        return;  // Точное попадание — не спускаемся к корню
                    }
                }

                // Fallback: спускаемся к объекту
                // g.unknown (не объявлен) → Visit(g) → тянет только "var g = ..."
                // g[x] (computed) → Visit(g) + Visit(x)
                Visit(me.Object);
                if (me.Computed)
                    Visit(me.Property);
                return;
            }

            if (node is Property prop)
            {
                if (prop.Computed)
                {
                    Visit(prop.Key);
                }
                Visit(prop.Value);
                return;
            }

            foreach (var child in node.ChildNodes)
            {
                Visit(child);
            }
        }

        private void DeclarePattern(Node node)
        {
            if (node is Identifier id)
            {
                DeclareLocal(id.Name);
            }
            else if (node is ObjectPattern op)
            {
                foreach (var prop in op.Properties)
                {
                    if (prop is Property p) DeclarePattern(p.Value);
                    else if (prop is RestElement r) DeclarePattern(r.Argument);
                }
            }
            else if (node is ArrayPattern ap)
            {
                foreach (var el in ap.Elements)
                {
                    if (el is not null) DeclarePattern(el);
                }
            }
            else if (node is AssignmentPattern asp)
            {
                DeclarePattern(asp.Left);
            }
            else if (node is RestElement re)
            {
                DeclarePattern(re.Argument);
            }
        }
    }

    private static (List<string> N, List<string> Sig) GetSolutions(List<Statement> statements, string baseJs)
    {
        var nList = new List<string>(2);
        var sigList = new List<string>(2);

        Log.Debug($"[AstSolver] Scanning {statements.Count} plain statements for solver extraction...");

        for (int i = 0; i < statements.Count; i++)
        {
            var solver = Extract(statements[i], baseJs);
            if (solver is not null)
            {
                nList.Add(MakeSolver(solver, "n"));
                sigList.Add(MakeSolver(solver, "sig"));
                Log.Info($"[AstSolver] Successfully extracted solver at statement index {i}");
            }
        }

        return (nList, sigList);
    }

    private static string? Extract(Node node, string baseJs)
    {
        var matchedId = AstMatcher.Matches(node, IdentifierTemplate);
        if (!matchedId) return null;

        Node? targetName = null;
        IReadOnlyList<Statement>? bodyStatements = null;

        if (node is FunctionDeclaration fd)
        {
            if (fd.Id is Identifier && fd.Body is not null)
            {
                targetName = fd.Id;
                bodyStatements = fd.Body.Body;
            }
        }
        else if (node is ExpressionStatement es)
        {
            if (es.Expression is AssignmentExpression ae && ae.Right is FunctionExpression fe && fe.Body is not null)
            {
                if (ae.Left is Identifier || ae.Left is MemberExpression)
                {
                    targetName = ae.Left;
                    bodyStatements = fe.Body.Body;
                }
            }
        }
        else if (node is VariableDeclaration vd)
        {
            for (int i = 0; i < vd.Declarations.Count; i++)
            {
                var decl = vd.Declarations[i];
                if (decl.Id is Identifier && decl.Init is FunctionExpression fe && fe.Body is not null)
                {
                    targetName = decl.Id;
                    bodyStatements = fe.Body.Body;
                    break;
                }
            }
        }

        if (targetName is not null && bodyStatements is not null)
        {
            bool hasAsdasd = false;
            for (int i = 0; i < bodyStatements.Count; i++)
            {
                if (AstMatcher.Matches(bodyStatements[i], AsdasdTemplate))
                {
                    hasAsdasd = true;
                    break;
                }
            }

            if (hasAsdasd)
            {
                int start = targetName.Start;
                int end = targetName.End;
                var expressionCode = baseJs[start..end];
                return CreateSolver(expressionCode);
            }
        }

        return null;
    }

    /// <summary>
    /// Создает анонимную JS-функцию обхода обфускации.
    /// Метод гарантирует немедленный выход (break) при первой успешной дешифрации, 
    /// что предотвращает повторное искажение раскодированных токенов.
    /// </summary>
    /// <param name="expressionCode">Сигнатура извлеченного метода.</param>
    /// <returns>JS-код солвера.</returns>
    private static string CreateSolver(string expressionCode)
    {
        return $$"""
        ({sig, n}) => {
          const decodedSig = sig ? (sig.indexOf('%') >= 0 ? decodeURIComponent(sig) : sig) : undefined;
          const url = ({{expressionCode}})("https://www.youtube.com/watch?v=bMD38TVNUF8", "s", decodedSig);
          if (n) {
            url.set("n", n);
          }
          
          let keys = [];
          let curr = url;
          while (curr && curr !== Object.prototype) {
              keys = keys.concat(Object.getOwnPropertyNames(curr));
              curr = Object.getPrototypeOf(curr);
          }
          
          const prevS = url.get("s");
          const prevN = url.get("n");
          
          if ((decodedSig !== undefined && prevS !== decodedSig) || (n !== undefined && prevN !== n)) {
              // Constructor already decrypted the parameters
          } else {
              for (const key of keys) {
                if (["constructor", "set", "get", "clone", "toString", "toJSON"].includes(key)) continue;
                if (typeof url[key] === "function") {
                  try { 
                    url[key](); 
                    
                    const sAfter = url.get("s");
                    const nAfter = url.get("n");
                    if ((decodedSig !== undefined && sAfter !== decodedSig) || (n !== undefined && nAfter !== n)) {
                        break; // CRITICAL: Stop calling subsequent functions to avoid re-scrambling!
                    }
                  } catch (e) {}
                }
              }
          }
          
          const s = url.get("s");
          return {
            sig: s ? (s.indexOf('%') >= 0 ? decodeURIComponent(s) : s) : null,
            n: url.get("n") ?? null
          };
        }
        """;
    }

    private static string MakeSolver(string resultArrowFunc, string identName)
    {
        return $$"""
        ({{identName}}) => {
          return ({{resultArrowFunc}})({
            {{identName}}: {{identName}}
          }).{{identName}};
        }
        """;
    }

    private static void AppendSolverAssignments(StringBuilder sb, string resultKey, List<string> solvers)
    {
        if (solvers.Count == 0)
        {
            Log.Warn($"[AstSolver] Warning: No solvers extracted for key '{resultKey}'");
            return;
        }

        sb.Append("globalThis._result.").Append(resultKey).AppendLine(" = (_input) => {");
        sb.AppendLine("  const _results = new Set();");
        sb.AppendLine("  const errors = [];");
        sb.AppendLine("  const _generators = [");
        for (int i = 0; i < solvers.Count; i++)
        {
            sb.Append(solvers[i]);
            if (i < solvers.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.AppendLine("  ];");
        sb.AppendLine("  for (var i = 0; i < _generators.length; i++) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      var res = _generators[i](_input);");
        sb.AppendLine("      _results.add(res);");
        sb.AppendLine("    } catch (e) {");
        sb.AppendLine("      errors.push(e);");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  if (!_results.size) {");
        sb.AppendLine("    throw `no solutions: ${errors.join(', ')}`;");
        sb.AppendLine("  }");
        sb.AppendLine("  if (_results.size !== 1) {");
        sb.AppendLine("    throw `invalid solutions: ${[..._results].map(x => JSON.stringify(x)).join(', ')}`;");
        sb.AppendLine("  }");
        sb.AppendLine("  return _results.values().next().value;");
        sb.AppendLine("};");
    }

    /// <summary>
    /// Извлекает SignatureTimestamp (STS) из кода плеера с использованием скомпилированного регулярного выражения.
    /// </summary>
    public static string ExtractSts(string baseJs)
    {
        var stsMatch = StsRegex().Match(baseJs);
        return stsMatch.Success ? stsMatch.Groups[1].Value : "1337";
    }

    [GeneratedRegex(@"(?:signatureTimestamp|sts)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StsRegex();
}