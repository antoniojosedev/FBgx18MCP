/*
				   File: type_SdtRCE06829400
			Description: RCE06829400
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
	[XmlRoot(ElementName="RCE06829400")]
	[XmlType(TypeName="RCE06829400" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRCE06829400 : GxUserType
	{
		public SdtRCE06829400( )
		{
			/* Constructor for serialization */
		}

		public SdtRCE06829400(IGxContext context)
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

			if (gxTv_SdtRCE06829400_Rows != null)
			{
				AddObjectProperty("Rows", gxTv_SdtRCE06829400_Rows, false);
			}
			return;
		}
		#endregion

		#region Properties

		[SoapElement(ElementName="Amount")]
		[XmlElement(ElementName="Amount")]
		public short gxTpr_Amount
		{
			get {
				return gxTv_SdtRCE06829400_Amount; 
			}
			set {
				gxTv_SdtRCE06829400_Amount = value;
				SetDirty("Amount");
			}
		}




		[SoapElement(ElementName="Rows" )]
		[XmlArray(ElementName="Rows"  )]
		[XmlArrayItemAttribute(ElementName="Entry" , IsNullable=false )]
		public GXBaseCollection<SdtRCE06829400_Entry> gxTpr_Rows
		{
			get {
				if ( gxTv_SdtRCE06829400_Rows == null )
				{
					gxTv_SdtRCE06829400_Rows = new GXBaseCollection<SdtRCE06829400_Entry>( context, "RCE06829400.Entry", "");
				}
				return gxTv_SdtRCE06829400_Rows;
			}
			set {
				gxTv_SdtRCE06829400_Rows_N = false;
				gxTv_SdtRCE06829400_Rows = value;
				SetDirty("Rows");
			}
		}

		public void gxTv_SdtRCE06829400_Rows_SetNull()
		{
			gxTv_SdtRCE06829400_Rows_N = true;
			gxTv_SdtRCE06829400_Rows = null;
		}

		public bool gxTv_SdtRCE06829400_Rows_IsNull()
		{
			return gxTv_SdtRCE06829400_Rows == null;
		}
		public bool ShouldSerializegxTpr_Rows_GxSimpleCollection_Json()
		{
			return gxTv_SdtRCE06829400_Rows != null && gxTv_SdtRCE06829400_Rows.Count > 0;

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
			gxTv_SdtRCE06829400_Rows_N = true;

			return  ;
		}



		#endregion

		#region Declaration

		protected short gxTv_SdtRCE06829400_Amount;
		 
		protected bool gxTv_SdtRCE06829400_Rows_N;
		protected GXBaseCollection<SdtRCE06829400_Entry> gxTv_SdtRCE06829400_Rows = null; 



		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"RCE06829400", Namespace="KBTeste")]
	public class SdtRCE06829400_RESTInterface : GxGenericCollectionItem<SdtRCE06829400>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRCE06829400_RESTInterface( ) : base()
		{	
		}

		public SdtRCE06829400_RESTInterface( SdtRCE06829400 psdt ) : base(psdt)
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

		[DataMember(Name="Rows", Order=1, EmitDefaultValue=false)]
		public GxGenericCollection<SdtRCE06829400_Entry_RESTInterface> gxTpr_Rows
		{
			get {
				if (sdt.ShouldSerializegxTpr_Rows_GxSimpleCollection_Json())
					return new GxGenericCollection<SdtRCE06829400_Entry_RESTInterface>(sdt.gxTpr_Rows);
				else
					return null;

			}
			set {
				value.LoadCollection(sdt.gxTpr_Rows);
			}
		}


		#endregion

		public SdtRCE06829400 sdt
		{
			get { 
				return (SdtRCE06829400)Sdt;
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
				sdt = new SdtRCE06829400() ;
			}
		}
	}
	#endregion
}