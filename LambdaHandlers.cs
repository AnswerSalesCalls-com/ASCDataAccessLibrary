using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace ASCTableStorage.Models
{
    /// <summary>
    /// Result of expression analysis, containing server and client parts
    /// </summary>
    internal class ExpressionVisitorResult
    {
        public Expression? ServerExpression { get; set; }
        public Expression? ClientExpression { get; set; }
    }

    /// <summary>
    /// Base class for expression analysis that properly separates server/client operations
    /// </summary>
    internal abstract class ExpressionAnalyzerBase<T> : ExpressionVisitor
    {
        private readonly List<Expression> _serverParts = new();
        private readonly List<Expression> _clientParts = new();
        protected ParameterExpression? Parameter;

        /// <summary>
        /// Analyzes the predicate and splits it into server and client parts
        /// </summary>
        public ExpressionVisitorResult Analyze(Expression<Func<T, bool>> predicate)
        {
            Parameter = predicate.Parameters[0];
            _serverParts.Clear();
            _clientParts.Clear();

            // Visit the expression tree to categorize parts
            Visit(predicate.Body);

            return new ExpressionVisitorResult
            {
                ServerExpression = RebuildExpression(_serverParts),
                ClientExpression = RebuildExpression(_clientParts)
            };
        }

        #region Abstract Methods - Must be implemented by derived classes

        /// <summary>
        /// Determines if a specific method call is supported server-side
        /// </summary>
        protected abstract bool IsMethodSupported(MethodCallExpression node);

        /// <summary>
        /// Generates the filter string from an expression
        /// </summary>
        public abstract string GenerateFilter(Expression? expression);

        #endregion

        #region Analysis Visit Methods - These do the categorization

        protected override Expression VisitBinary(BinaryExpression node)
        {
            // Handle logical operators specially
            if (node.NodeType == ExpressionType.AndAlso || node.NodeType == ExpressionType.OrElse)
            {
                // Recursively visit both sides
                Visit(node.Left);
                Visit(node.Right);
                return node;
            }

            // For other binary operations, check if they can be server-side
            if (IsServerSide(node))
            {
                _serverParts.Add(node);
            }
            else
            {
                _clientParts.Add(node);
            }

            return node;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // Check both if it's server-side capable AND if this specific method is supported
            if (IsServerSide(node) && IsMethodSupported(node))
            {
                _serverParts.Add(node);
            }
            else
            {
                _clientParts.Add(node);
            }

            return node;
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Not && IsServerSide(node.Operand))
            {
                _serverParts.Add(node);
            }
            else
            {
                _clientParts.Add(node);
            }

            return node;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Determines if an expression can be evaluated server-side
        /// </summary>
        private bool IsServerSide(Expression node)
        {
            // First check if the expression involves the parameter
            if (!new ParameterFinder(Parameter!).IsParameterPresent(node))
                return false;

            // Then check if all non-parameter parts can be resolved to values
            return new ValueResolvingVisitor(Parameter!).CanBeResolved(node);
        }

        /// <summary>
        /// Rebuilds multiple expressions into a single AND expression
        /// </summary>
        private Expression? RebuildExpression(List<Expression> parts)
        {
            if (!parts.Any()) return null;

            // Combine all parts with AND
            return parts.Aggregate((left, right) => Expression.AndAlso(left, right));
        }

        /// <summary>
        /// Attempts to resolve an expression to a concrete value
        /// </summary>
        protected static bool TryResolveValue(Expression? expression, out object? value)
        {
            value = null;
            if (expression == null) return false;

            switch (expression.NodeType)
            {
                case ExpressionType.Constant:
                    value = ((ConstantExpression)expression).Value;
                    return true;

                case ExpressionType.MemberAccess:
                    var memberExpr = (MemberExpression)expression;
                    if (TryResolveValue(memberExpr.Expression, out var parentValue))
                    {
                        if (parentValue == null) return false;

                        if (memberExpr.Member is FieldInfo field)
                        {
                            value = field.GetValue(parentValue);
                            return true;
                        }
                        if (memberExpr.Member is PropertyInfo property)
                        {
                            value = property.GetValue(parentValue);
                            return true;
                        }
                    }
                    break;

                default:
                    // Try to compile and execute the expression
                    try
                    {
                        value = Expression.Lambda(expression).Compile().DynamicInvoke();
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
            }

            return false;
        }

        #endregion

        #region Helper Visitor Classes

        /// <summary>
        /// Checks if all non-parameter parts of an expression can be resolved
        /// </summary>
        private class ValueResolvingVisitor : ExpressionVisitor
        {
            private readonly ParameterExpression _dbParameter;
            private bool _canBeResolved = true;

            public ValueResolvingVisitor(ParameterExpression dbParameter)
            {
                _dbParameter = dbParameter;
            }

            public bool CanBeResolved(Expression node)
            {
                _canBeResolved = true;
                Visit(node);
                return _canBeResolved;
            }

            public override Expression? Visit(Expression? node)
            {
                return _canBeResolved ? base.Visit(node) : node;
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (!IsParameterPath(node))
                {
                    if (!TryResolveValue(node, out _))
                    {
                        _canBeResolved = false;
                    }
                }
                return base.VisitMember(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // CRITICAL FIX: Check the object the method is called on
                if (node.Object != null && !IsParameterPath(node.Object))
                {
                    if (!TryResolveValue(node.Object, out _))
                    {
                        _canBeResolved = false;
                        return node;
                    }
                }

                // Also check all arguments
                foreach (var arg in node.Arguments)
                {
                    if (!IsParameterPath(arg))
                    {
                        if (!TryResolveValue(arg, out _))
                        {
                            _canBeResolved = false;
                            return node;
                        }
                    }
                }

                return base.VisitMethodCall(node);
            }

            private bool IsParameterPath(Expression? expression)
            {
                while (expression is MemberExpression me)
                {
                    expression = me.Expression;
                }
                return expression == _dbParameter;
            }
        }

        /// <summary>
        /// Finds if a parameter is present in an expression
        /// </summary>
        private class ParameterFinder : ExpressionVisitor
        {
            private readonly ParameterExpression _parameterToFind;
            public bool FoundParameter { get; private set; }

            public ParameterFinder(ParameterExpression p)
            {
                _parameterToFind = p;
            }

            public bool IsParameterPresent(Expression node)
            {
                FoundParameter = false;
                Visit(node);
                return FoundParameter;
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _parameterToFind)
                    FoundParameter = true;
                return base.VisitParameter(node);
            }
        }

        #endregion
    }

    /// <summary>
    /// OData filter visitor for Azure Table Storage with limited method support
    /// </summary>
    internal class ODataFilterVisitor<T> : ExpressionAnalyzerBase<T> where T : TableEntityBase, ITableExtra, new()
    {
        /// <summary>
        /// Determines which methods are supported by the old Azure Table Storage SDK
        /// </summary>
        protected override bool IsMethodSupported(MethodCallExpression node)
        {
            // The old Microsoft.Azure.Cosmos.Table SDK has very limited method support
            // Only static string methods like IsNullOrEmpty are supported
            if (node.Method.IsStatic &&
                node.Method.DeclaringType == typeof(string) &&
                (node.Method.Name == "IsNullOrEmpty" || node.Method.Name == "IsNullOrWhiteSpace"))
            {
                return true;
            }

            // All other methods (Contains, ToUpper, etc.) are NOT supported
            return false;
        }

        /// <summary>
        /// Generates OData filter string from expression
        /// </summary>
        public override string GenerateFilter(Expression? expression)
        {
            if (expression == null) return string.Empty;

            var generator = new ODataGenerator();
            generator.Visit(expression);
            return generator.GetFilter();
        }

        /// <summary>
        /// Nested class to handle OData string generation
        /// </summary>
        private class ODataGenerator : ExpressionVisitor
        {
            private readonly StringBuilder _filter = new();

            public string GetFilter() => _filter.ToString();

            protected override Expression VisitBinary(BinaryExpression node)
            {
                _filter.Append('(');
                Visit(node.Left);
                _filter.Append($" {ConvertOperator(node.NodeType)} ");
                Visit(node.Right);
                _filter.Append(')');
                return node;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.Name == "IsNullOrEmpty" || node.Method.Name == "IsNullOrWhiteSpace")
                {
                    _filter.Append('(');
                    Visit(node.Arguments[0]);
                    _filter.Append(" eq null or ");
                    Visit(node.Arguments[0]);
                    _filter.Append(" eq ''");
                    _filter.Append(')');
                }
                return node;
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression?.NodeType == ExpressionType.Parameter)
                {
                    _filter.Append(node.Member.Name);
                }
                else if (node.Expression is MemberExpression parentMember &&
                         (parentMember.Type == typeof(DateTime) || parentMember.Type == typeof(DateTime?)))
                {
                    _filter.Append($"{node.Member.Name.ToLowerInvariant()}({parentMember.Member.Name})");
                }
                else
                {
                    Visit(Expression.Constant(GetValue(node)));
                }
                return node;
            }

            protected override Expression VisitConstant(ConstantExpression node)
            {
                var value = node.Value;
                if (value == null)
                {
                    _filter.Append("null");
                }
                else if (value is string s)
                {
                    _filter.Append($"'{s.Replace("'", "''")}'");
                }
                else if (value is DateTime dt)
                {
                    _filter.Append($"datetime'{dt:yyyy-MM-ddTHH:mm:ss.fffZ}'");
                }
                else if (value is bool b)
                {
                    _filter.Append(b ? "true" : "false");
                }
                else if (value is Guid g)
                {
                    _filter.Append($"guid'{g}'");
                }
                else
                {
                    _filter.Append(value.ToString());
                }
                return node;
            }

            private static object? GetValue(Expression exp)
            {
                if (exp is ConstantExpression c) return c.Value;
                return Expression.Lambda(exp).Compile().DynamicInvoke();
            }

            private string ConvertOperator(ExpressionType t) => t switch
            {
                ExpressionType.Equal => "eq",
                ExpressionType.NotEqual => "ne",
                ExpressionType.GreaterThan => "gt",
                ExpressionType.GreaterThanOrEqual => "ge",
                ExpressionType.LessThan => "lt",
                ExpressionType.LessThanOrEqual => "le",
                ExpressionType.AndAlso => "and",
                ExpressionType.OrElse => "or",
                _ => throw new NotSupportedException($"Operator {t} is not supported in OData queries"),
            };
        }
    }

    /// <summary>
    /// Blob tag filter visitor for Azure Blob Storage with extended method support
    /// </summary>
    internal class BlobTagFilterVisitor<T> : ExpressionAnalyzerBase<T>
    {
        /// <summary>
        /// Determines which methods are supported by Azure Blob tag queries
        /// </summary>
        protected override bool IsMethodSupported(MethodCallExpression node)
        {
            // String methods: Contains, StartsWith, EndsWith
            if (node.Object?.Type == typeof(string) &&
                (node.Method.Name == "Contains" || node.Method.Name == "StartsWith" || node.Method.Name == "EndsWith"))
            {
                return true;
            }

            // List.Contains method
            if (node.Method.Name == "Contains" &&
                node.Arguments.Count > 0 &&
                node.Arguments.Last().NodeType == ExpressionType.MemberAccess)
            {
                return true;
            }

            // Tags indexer: Tags["key"]
            if (node.Method.Name == "get_Item" &&
                node.Object is MemberExpression me &&
                me.Member.Name == "Tags")
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Generates blob tag filter string from expression
        /// </summary>
        public override string GenerateFilter(Expression? expression)
        {
            if (expression == null) return string.Empty;

            var generator = new BlobTagGenerator();
            generator.Visit(expression);
            return generator.GetFilter();
        }

        /// <summary>
        /// Nested class to handle blob tag string generation
        /// </summary>
        private class BlobTagGenerator : ExpressionVisitor
        {
            private readonly StringBuilder _filter = new();

            public string GetFilter() => _filter.ToString();

            protected override Expression VisitBinary(BinaryExpression node)
            {
                _filter.Append('(');
                Visit(node.Left);
                _filter.Append($" {ConvertOperator(node.NodeType)} ");
                Visit(node.Right);
                _filter.Append(')');
                return node;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // Handle Tags["key"] indexer
                if (node.Method.Name == "get_Item" &&
                    node.Object is MemberExpression me &&
                    me.Member.Name == "Tags" &&
                    node.Arguments[0] is ConstantExpression ce)
                {
                    _filter.Append($"\"{ce.Value}\"");
                    return node;
                }

                // Handle string methods
                if (node.Object?.Type == typeof(string))
                {
                    var pattern = node.Method.Name switch
                    {
                        "Contains" => "%{0}%",
                        "StartsWith" => "{0}%",
                        "EndsWith" => "%{0}",
                        _ => null
                    };

                    if (pattern != null && GetValue(node.Arguments[0]) is string value)
                    {
                        Visit(node.Object);
                        _filter.Append($" LIKE '{string.Format(pattern, value.Replace("'", "''"))}'");
                        return node;
                    }
                }

                // Handle list.Contains(item)
                if (node.Method.Name == "Contains" &&
                    node.Arguments.Last() is MemberExpression dbProp &&
                    GetValue(node.Object ?? node.Arguments.First()) is IEnumerable list)
                {
                    var orParts = new List<string>();
                    foreach (var item in list)
                    {
                        orParts.Add($"\"{dbProp.Member.Name}\" = '{item?.ToString()?.Replace("'", "''")}'");
                    }

                    if (orParts.Any())
                    {
                        _filter.Append($"({string.Join(" OR ", orParts)})");
                    }
                    return node;
                }

                return node;
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression?.NodeType == ExpressionType.Parameter)
                {
                    _filter.Append($"\"{node.Member.Name}\"");
                }
                else
                {
                    Visit(Expression.Constant(GetValue(node)));
                }
                return node;
            }

            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (node.Value == null)
                {
                    throw new NotSupportedException("Null values are not supported in Blob Tag queries.");
                }

                _filter.Append($"'{node.Value.ToString()?.Replace("'", "''")}'");
                return node;
            }

            private static object? GetValue(Expression e)
            {
                if (e is ConstantExpression c) return c.Value;
                return Expression.Lambda(e).Compile().DynamicInvoke();
            }

            private string ConvertOperator(ExpressionType t) => t switch
            {
                ExpressionType.Equal => "=",
                ExpressionType.NotEqual => "!=",
                ExpressionType.GreaterThan => ">",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThan => "<",
                ExpressionType.LessThanOrEqual => "<=",
                ExpressionType.AndAlso => "AND",
                ExpressionType.OrElse => "OR",
                _ => throw new NotSupportedException($"Operator {t} is not supported in Blob Tag queries"),
            };
        }
    }

    /// <summary>
    /// A generic expression visitor that traverses an expression tree and replaces all occurrences
    /// of a specific expression node with a new one.
    /// </summary>
    /// <remarks>
    /// This is useful for rebuilding a lambda expression's body by substituting a analyzed part (e.g., a client-side filter),
    /// ensuring the resulting tree is clean for compilation by the .NET framework.
    /// </remarks>
    internal class ExpressionReplacer : ExpressionVisitor
    {
        private readonly Expression _oldExpression;
        private readonly Expression _newExpression;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExpressionReplacer"/> class.
        /// </summary>
        /// <param name="oldExpression">The expression node to be replaced.</param>
        /// <param name="newExpression">The expression node to substitute in its place.</param>
        public ExpressionReplacer(Expression oldExpression, Expression newExpression)
        {
            _oldExpression = oldExpression;
            _newExpression = newExpression;
        }

        /// <summary>
        /// Visits the specified expression node, replacing it if it matches the target.
        /// </summary>
        /// <param name="node">The expression node to visit.</param>
        /// <returns>The original node, the new node if it's a match, or a visited version of the original node.</returns>
        public override Expression? Visit(Expression? node)
        {
            return node == _oldExpression ? _newExpression : base.Visit(node);
        }
    }
}