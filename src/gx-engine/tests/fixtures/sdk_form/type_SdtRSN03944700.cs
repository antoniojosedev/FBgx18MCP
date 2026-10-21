/*
				   File: type_SdtRSN03944700
			Description: RSN03944700
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
	[XmlRoot(ElementName="RSN03944700")]
	[XmlType(TypeName="RSN03944700" , Namespace="KBTeste" )]
	[Serializable]
	public class SdtRSN03944700 : GxUserType
	{
		public SdtRSN03944700( )
		{
			/* Constructor for serialization */
			gxTv_SdtRSN03944700_Label = "";

			gxTv_SdtRSN03944700_Fixed = "";

		}

		public SdtRSN03944700(IGxContext context)
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


			AddObjectProperty("Label", gxTpr_Label, false);


			AddObjectProperty("Fixed", gxTpr_Fixed, false);

			if (gxTv_SdtRSN03944700_Child != null)
			{
				AddObjectProperty("Child", gxTv_SdtRSN03944700_Child, false);
			}
			if (gxTv_SdtRSN03944700_Rows != null)
			{
				AddObjectProperty("Rows", gxTv_SdtRSN03944700_Rows, false);
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
				return gxTv_SdtRSN03944700_Amount; 
			}
			set {
				gxTv_SdtRSN03944700_Amount = value;
				SetDirty("Amount");
			}
		}




		[SoapElement(ElementName="Label")]
		[XmlElement(ElementName="Label")]
		public string gxTpr_Label
		{
			get {
				return gxTv_SdtRSN03944700_Label; 
			}
			set {
				gxTv_SdtRSN03944700_Label = value;
				SetDirty("Label");
			}
		}




		[SoapElement(ElementName="Fixed")]
		[XmlElement(ElementName="Fixed")]
		public string gxTpr_Fixed
		{
			get {
				return gxTv_SdtRSN03944700_Fixed; 
			}
			set {
				gxTv_SdtRSN03944700_Fixed = value;
				SetDirty("Fixed");
			}
		}



		[SoapElement(ElementName="Child" )]
		[XmlElement(ElementName="Child" )]
		public SdtRSN03944700_Child gxTpr_Child
		{
			get {
				if ( gxTv_SdtRSN03944700_Child == null )
				{
					gxTv_SdtRSN03944700_Child = new SdtRSN03944700_Child(context);
				}
				gxTv_SdtRSN03944700_Child_N = false;
				return gxTv_SdtRSN03944700_Child;
			}
			set {
				gxTv_SdtRSN03944700_Child_N = false;
				gxTv_SdtRSN03944700_Child = value;
				SetDirty("Child");
			}

		}

		public void gxTv_SdtRSN03944700_Child_SetNull()
		{
			gxTv_SdtRSN03944700_Child_N = true;
			gxTv_SdtRSN03944700_Child = null;
		}

		public bool gxTv_SdtRSN03944700_Child_IsNull()
		{
			return gxTv_SdtRSN03944700_Child == null;
		}
		public bool ShouldSerializegxTpr_Child_Json()
		{
				return (gxTv_SdtRSN03944700_Child != null && gxTv_SdtRSN03944700_Child.ShouldSerializeSdtJson());

		}



		[SoapElement(ElementName="Rows" )]
		[XmlArray(ElementName="Rows"  )]
		[XmlArrayItemAttribute(ElementName="RowsItem" , IsNullable=false )]
		public GXBaseCollection<SdtRSN03944700_RowsItem> gxTpr_Rows
		{
			get {
				if ( gxTv_SdtRSN03944700_Rows == null )
				{
					gxTv_SdtRSN03944700_Rows = new GXBaseCollection<SdtRSN03944700_RowsItem>( context, "RSN03944700.RowsItem", "");
				}
				return gxTv_SdtRSN03944700_Rows;
			}
			set {
				gxTv_SdtRSN03944700_Rows_N = false;
				gxTv_SdtRSN03944700_Rows = value;
				SetDirty("Rows");
			}
		}

		public void gxTv_SdtRSN03944700_Rows_SetNull()
		{
			gxTv_SdtRSN03944700_Rows_N = true;
			gxTv_SdtRSN03944700_Rows = null;
		}

		public bool gxTv_SdtRSN03944700_Rows_IsNull()
		{
			return gxTv_SdtRSN03944700_Rows == null;
		}
		public bool ShouldSerializegxTpr_Rows_GxSimpleCollection_Json()
		{
			return gxTv_SdtRSN03944700_Rows != null && gxTv_SdtRSN03944700_Rows.Count > 0;

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
			gxTv_SdtRSN03944700_Label = "";
			gxTv_SdtRSN03944700_Fixed = "";

			gxTv_SdtRSN03944700_Child_N = true;


			gxTv_SdtRSN03944700_Rows_N = true;

			return  ;
		}



		#endregion

		#region Declaration

		protected short gxTv_SdtRSN03944700_Amount;
		 

		protected string gxTv_SdtRSN03944700_Label;
		 

		protected string gxTv_SdtRSN03944700_Fixed;
		 
		protected bool gxTv_SdtRSN03944700_Child_N;
		protected SdtRSN03944700_Child gxTv_SdtRSN03944700_Child = null; 

		protected bool gxTv_SdtRSN03944700_Rows_N;
		protected GXBaseCollection<SdtRSN03944700_RowsItem> gxTv_SdtRSN03944700_Rows = null; 



		#endregion
	}
	#region Rest interface
	[GxJsonSerialization("default")]
	[DataContract(Name=@"RSN03944700", Namespace="KBTeste")]
	public class SdtRSN03944700_RESTInterface : GxGenericCollectionItem<SdtRSN03944700>, System.Web.SessionState.IRequiresSessionState
	{
		public SdtRSN03944700_RESTInterface( ) : base()
		{	
		}

		public SdtRSN03944700_RESTInterface( SdtRSN03944700 psdt ) : base(psdt)
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

		[DataMember(Name="Label", Order=1)]
		public  string gxTpr_Label
		{
			get { 
				return sdt.gxTpr_Label;

			}
			set { 
				 sdt.gxTpr_Label = value;
			}
		}

		[DataMember(Name="Fixed", Order=2)]
		public  string gxTpr_Fixed
		{
			get { 
				return StringUtil.RTrim( sdt.gxTpr_Fixed);

			}
			set { 
				 sdt.gxTpr_Fixed = value;
			}
		}

		[DataMember(Name="Child", Order=3, EmitDefaultValue=false)]
		public SdtRSN03944700_Child_RESTInterface gxTpr_Child
		{
			get {
				if (sdt.ShouldSerializegxTpr_Child_Json())
					return new SdtRSN03944700_Child_RESTInterface(sdt.gxTpr_Child);
				else
					return null;

			}

			set {
				sdt.gxTpr_Child = value.sdt;
			}

		}

		[DataMember(Name="Rows", Order=4, EmitDefaultValue=false)]
		public GxGenericCollection<SdtRSN03944700_RowsItem_RESTInterface> gxTpr_Rows
		{
			get {
				if (sdt.ShouldSerializegxTpr_Rows_GxSimpleCollection_Json())
					return new GxGenericCollection<SdtRSN03944700_RowsItem_RESTInterface>(sdt.gxTpr_Rows);
				else
					return null;

			}
			set {
				value.LoadCollection(sdt.gxTpr_Rows);
			}
		}


		#endregion

		public SdtRSN03944700 sdt
		{
			get { 
				return (SdtRSN03944700)Sdt;
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
				sdt = new SdtRSN03944700() ;
			}
		}
	}
	#endregion
}