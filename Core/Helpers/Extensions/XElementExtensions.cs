using System.Xml.Linq;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для элементов <see cref="XElement"/>.
/// </summary>
internal static class XElementExtensions
{
    extension(XElement element)
    {
        /// <summary>
        /// Создает копию XML-элемента с полным удалением всех пространств имен и их атрибутов.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="element"/> равен null.</exception>
        public XElement StripNamespaces()
        {
            ArgumentNullException.ThrowIfNull(element);

            var result = new XElement(element);

            foreach (var descendantElement in result.DescendantsAndSelf())
            {
                descendantElement.Name = XNamespace.None.GetName(descendantElement.Name.LocalName);

                var filtered = new List<XAttribute>();
                foreach (var a in descendantElement.Attributes())
                {
                    if (a.IsNamespaceDeclaration) continue;
                    if (a.Name.Namespace == XNamespace.Xml || a.Name.Namespace == XNamespace.Xmlns) continue;

                    filtered.Add(new XAttribute(
                        XNamespace.None.GetName(a.Name.LocalName),
                        a.Value));
                }

                descendantElement.ReplaceAttributes(filtered);
            }

            return result;
        }
    }
}