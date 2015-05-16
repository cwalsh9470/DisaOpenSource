using System;

namespace Raksha.Asn1.Cmp
{
	public class PkiConfirmContent
		: Asn1Encodable
	{
		public static PkiConfirmContent GetInstance(object obj)
		{
			if (obj is PkiConfirmContent)
				return (PkiConfirmContent)obj;

			if (obj is Asn1Null)
				return new PkiConfirmContent();

            throw new ArgumentException("Invalid object: " + obj.GetType().Name, "obj");
		}

		public PkiConfirmContent()
		{
		}

		/**
		 * <pre>
		 * PkiConfirmContent ::= NULL
		 * </pre>
		 * @return a basic ASN.1 object representation.
		 */
		public override Asn1Object ToAsn1Object()
		{
			return DerNull.Instance;
		}
	}
}
