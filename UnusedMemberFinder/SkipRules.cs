using Microsoft.CodeAnalysis;

namespace UnusedMemberFinder;

internal static class SkipRules
{
    public static bool ShouldSkip(ISymbol sym, bool includePublic)
    {
        if (!includePublic && sym.DeclaredAccessibility == Accessibility.Public)
            return true;

        // エントリポイント
        if (sym is IMethodSymbol { Name: "Main", IsStatic: true })
            return true;

        // override / インターフェース実装（framework や外部から呼ばれうる）
        if (sym is IMethodSymbol m && (m.IsOverride || IsInterfaceImpl(m)))
            return true;

        if (sym is IPropertySymbol p && (p.IsOverride || IsInterfaceImplProperty(p)))
            return true;

        // 属性付き（[Obsolete], [Fact], [Test], シリアライズ属性 等）
        if (sym.GetAttributes().Length > 0)
            return true;

        // コンストラクタ（DI・new 経由で使われる可能性）
        if (sym is IMethodSymbol { MethodKind: MethodKind.Constructor })
            return true;

        // デストラクタ / ファイナライザ
        if (sym is IMethodSymbol { MethodKind: MethodKind.Destructor })
            return true;

        // 演算子オーバーロード
        if (sym is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion })
            return true;

        // プロパティのアクセサは型メンバーとしてではなくプロパティ自体で評価するためスキップ
        if (sym is IMethodSymbol { AssociatedSymbol: IPropertySymbol })
            return true;

        // イベントのアクセサ
        if (sym is IMethodSymbol { AssociatedSymbol: IEventSymbol })
            return true;

        return false;
    }

    private static bool IsInterfaceImpl(IMethodSymbol m)
    {
        if (m.ExplicitInterfaceImplementations.Length > 0) return true;
        var type = m.ContainingType;
        return type.AllInterfaces.Any(iface =>
            iface.GetMembers(m.Name).OfType<IMethodSymbol>().Any(im =>
                type.FindImplementationForInterfaceMember(im)?.Equals(m, SymbolEqualityComparer.Default) == true));
    }

    private static bool IsInterfaceImplProperty(IPropertySymbol p)
    {
        if (p.ExplicitInterfaceImplementations.Length > 0) return true;
        var type = p.ContainingType;
        return type.AllInterfaces.Any(iface =>
            iface.GetMembers(p.Name).OfType<IPropertySymbol>().Any(ip =>
                type.FindImplementationForInterfaceMember(ip)?.Equals(p, SymbolEqualityComparer.Default) == true));
    }
}
