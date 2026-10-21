/*
				   File: type_SdtRSN03944700_Child
			Description: Child
				 Author: Nemo 🐠 for C# (.NET) version 18.0.10.184260
		   Program type: Callable routine
			  Main DBMS: 
*/
using System;
using System.Collections;
using GeneXus.Utils;
using GeneXus.Resources;
using GeneXus.Application;
using GeneXus.Metadata;
using GeneXus.Cryptography;
using GeneXus.Encryption;
using GeneXus.Http.Client;
using GeneXus.Http.Server;
using System.Reflection;
using System.Xml.Serialization;
using System.Runtime.Serialization;


namespace GeneXus.Programs
{
	[XmlRoot(ElementName="RSN03944700.Child")]
	[XmlType(TypeName="RSN03944700.Child" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRSN03944700_Child : GxUserType
	{
		public SdtRSN03944700_Child( )
		{
			/* Constructor for serialization */
		}

		public SdtRSN03944700_Child(IGxContext context)
		{
			this.context = context;	
			initialize();
		}

		#region Json
		private static Hashtable mapper;
		public override string JsonMap(string value)
		{
			if (mapper == null)
			{
				mapper = new Hashtable();
			}
			return (string)mapper[value]; ;
		}

		public override void ToJSON()
		{
			ToJSON(true) ;
			return;
		}

		public override void ToJSON(bool includeState)
		{
			AddObjectProperty("Amount", gxTpr_Amount, false);

			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="Amount")]
		[XmlElement(ElementName="Amount")]
		public short gxTpr_Amount
		{
			get {
				return gxTv_SdtRSN03944700_Child_Amount; 
			}
			set {
				gxTv_SdtRSN03944700_Child_Amount = value;
				SetDirty("Amount");
			}
		}



		public override bool ShouldSerializeSdtJson()
		{
			return true;
		}



		#endregion

		#region Static Type Properties

		[XmlIgnore]
		private static GXTypeInfo _typeProps;
		protected override GXTypeInfo TypeInfo { get { return _typeProps; } set { _typeProps = value; } }

		#endregion

		#region Initialization

		public void initialize( )
		{
			return  ;
		}



		#endregion

		#region Declaration

		protected short gxTv_SdtRSN03944700_Child_Amount;
		 


		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"RSN03944700.Child", Namespace="KBTeste")]
	public class SdtRSN03944700_Child_RESTInterface : GxGenericCollectionItem<SdtRSN03944700_Child>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRSN03944700_Child_RESTInterface( ) : base()
		{	
		}

		public SdtRSN03944700_Child_RESTInterface( SdtRSN03944700_Child psdt ) : base(psdt)
		{	
		}

		#region Rest Properties
		[DataMember(Name="Amount", Order=0)]
		public short gxTpr_Amount
		{
			get { 
				return sdt.gxTpr_Amount;

			}
			set { 
				sdt.gxTpr_Amount = value;
			}
		}


		#endregion

		public SdtRSN03944700_Child sdt
		{
			get { 
				return (SdtRSN03944700_Child)Sdt;
			}
			set { 
				Sdt = value;
			}
		}

		[OnDeserializing]
		void checkSdt( StreamingContext ctx )
		{
			if ( sdt == null )
			{
				sdt = new SdtRSN03944700_Child() ;
			}
		}
	}
	#endregion
}