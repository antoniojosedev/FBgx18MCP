using System;
using System.Collections;
using GeneXus.Utils;
using GeneXus.Resources;
using GeneXus.Application;
using GeneXus.Metadata;
using GeneXus.Cryptography;
using System.Data;
using GeneXus.Data;
using com.genexus;
using GeneXus.Data.ADO;
using GeneXus.Data.NTier;
using GeneXus.Data.NTier.ADO;
using GeneXus.WebControls;
using GeneXus.Http;
using GeneXus.XML;
using GeneXus.Search;
using GeneXus.Encryption;
using GeneXus.Http.Client;
using System.Xml.Serialization;
using System.Runtime.Serialization;
namespace GeneXus.Programs {
   public class livemoveattributerules2 : GXDataArea
   {
      protected void INITENV( )
      {
         if ( GxWebError != 0 )
         {
            return  ;
         }
      }

      protected void INITTRN( )
      {
         initialize_properties( ) ;
         entryPointCalled = false;
         gxfirstwebparm = GetNextPar( );
         gxfirstwebparm_bkp = gxfirstwebparm;
         gxfirstwebparm = DecryptAjaxCall( gxfirstwebparm);
         toggleJsOutput = isJsOutputEnabled( );
         if ( context.isSpaRequest( ) )
         {
            disableJsOutput();
         }
         if ( StringUtil.StrCmp(gxfirstwebparm, "dyncall") == 0 )
         {
            setAjaxCallMode();
            if ( ! IsValidAjaxCall( true) )
            {
               GxWebError = 1;
               return  ;
            }
            dyncall( GetNextPar( )) ;
            return  ;
         }
         else if ( StringUtil.StrCmp(gxfirstwebparm, "gxajaxEvt") == 0 )
         {
            setAjaxEventMode();
            if ( ! IsValidAjaxCall( true) )
            {
               GxWebError = 1;
               return  ;
            }
            gxfirstwebparm = GetNextPar( );
         }
         else if ( StringUtil.StrCmp(gxfirstwebparm, "gxfullajaxEvt") == 0 )
         {
            if ( ! IsValidAjaxCall( true) )
            {
               GxWebError = 1;
               return  ;
            }
            gxfirstwebparm = GetNextPar( );
         }
         else
         {
            if ( ! IsValidAjaxCall( false) )
            {
               GxWebError = 1;
               return  ;
            }
            gxfirstwebparm = gxfirstwebparm_bkp;
         }
         if ( toggleJsOutput )
         {
            if ( context.isSpaRequest( ) )
            {
               enableJsOutput();
            }
         }
         if ( String.IsNullOrEmpty(StringUtil.RTrim( context.GetCookie( "GX_SESSION_ID"))) )
         {
            gxcookieaux = context.SetCookie( "GX_SESSION_ID", Encrypt64( Crypto.GetEncryptionKey( ), Crypto.GetServerKey( )), "", (DateTime)(DateTime.MinValue), "", (short)(context.GetHttpSecure( )));
         }
         GXKey = Decrypt64( context.GetCookie( "GX_SESSION_ID"), Crypto.GetServerKey( ));
         toggleJsOutput = isJsOutputEnabled( );
         if ( context.isSpaRequest( ) )
         {
            disableJsOutput();
         }
         init_web_controls( ) ;
         if ( toggleJsOutput )
         {
            if ( context.isSpaRequest( ) )
            {
               enableJsOutput();
            }
         }
         if ( ! context.isSpaRequest( ) )
         {
            if ( context.ExposeMetadata( ) )
            {
               Form.Meta.addItem("generator", "GeneXus .NET 18_0_10-184260", 0) ;
            }
         }
         Form.Meta.addItem("description", "Live Move Attribute Rules2", 0) ;
         context.wjLoc = "";
         context.nUserReturn = 0;
         context.wbHandled = 0;
         if ( StringUtil.StrCmp(context.GetRequestMethod( ), "POST") == 0 )
         {
         }
         if ( ! context.isAjaxRequest( ) )
         {
            GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
            AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         }
         wbErr = false;
         context.SetDefaultTheme("KBTeste", true);
         if ( ! context.IsLocalStorageSupported( ) )
         {
            context.PushCurrentUrl();
         }
      }

      public livemoveattributerules2( )
      {
         context = new GxContext(  );
         DataStoreUtil.LoadDataStores( context);
         IsMain = true;
         context.SetDefaultTheme("KBTeste", true);
      }

      public livemoveattributerules2( IGxContext context )
      {
         this.context = context;
         IsMain = false;
      }

      public void execute( )
      {
         ExecuteImpl();
      }

      protected override void ExecutePrivate( )
      {
         isStatic = false;
         webExecute();
      }

      protected override void createObjects( )
      {
      }

      public override void webExecute( )
      {
         createObjects();
         initialize();
         INITENV( ) ;
         INITTRN( ) ;
         if ( ( GxWebError == 0 ) && ! isAjaxCallMode( ) )
         {
            MasterPageObj = (GXMasterPage) ClassLoader.GetInstance("general.ui.masterunanimosidebar", "GeneXus.Programs.general.ui.masterunanimosidebar", new Object[] {context});
            MasterPageObj.setDataArea(this,false);
            ValidateSpaRequest();
            MasterPageObj.webExecute();
            if ( ( GxWebError == 0 ) && context.isAjaxRequest( ) )
            {
               enableOutput();
               if ( ! context.isAjaxRequest( ) )
               {
                  context.GX_webresponse.AppendHeader("Cache-Control", "no-store");
               }
               if ( ! context.WillRedirect( ) )
               {
                  AddString( context.getJSONResponse( )) ;
               }
               else
               {
                  if ( context.isAjaxRequest( ) )
                  {
                     disableOutput();
                  }
                  RenderHtmlHeaders( ) ;
                  context.Redirect( context.wjLoc );
                  context.DispatchAjaxCommands();
               }
            }
         }
         cleanup();
      }

      protected void fix_multi_value_controls( )
      {
      }

      protected void Draw( )
      {
         if ( context.isAjaxRequest( ) )
         {
            disableOutput();
         }
         if ( ! GxWebStd.gx_redirect( context) )
         {
            disable_std_buttons( ) ;
            enableDisable( ) ;
            set_caption( ) ;
            /* Form start */
            DrawControls( ) ;
            fix_multi_value_controls( ) ;
         }
         /* Execute Exit event if defined. */
      }

      protected void DrawControls( )
      {
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "Section", "start", "top", " "+"data-gx-base-lib=\"none\""+" "+"data-abstract-form"+" ", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, divMaintable_Internalname, 1, 0, "px", 0, "px", "Table", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, divTitlecontainer_Internalname, 1, 0, "px", 0, "px", "title-container", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12", "start", "top", "", "", "div");
         /* Text block */
         GxWebStd.gx_label_ctrl( context, lblTitle_Internalname, "Live Move Attribute Rules2", "", "", lblTitle_Jsonclick, "'"+""+"'"+",false,"+"'"+""+"'", "", "heading-01", 0, "", 1, 1, 0, 0, "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12", "start", "top", "", "", "div");
         ClassString = "ErrorViewer";
         StyleString = "";
         GxWebStd.gx_msg_list( context, "", context.GX_msglist.DisplayMode, StyleString, ClassString, "", "false");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, divFormcontainer_Internalname, 1, 0, "px", 0, "px", "form-container", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, divToolbarcell_Internalname, 1, 0, "px", 0, "px", "col-xs-12 col-sm-9 col-sm-offset-3 form__toolbar-cell", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-action-group ActionGroup", "start", "top", " "+"data-gx-actiongroup-type=\"toolbar\""+" ", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "btn-group", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 21,'',false,'',0)\"";
         ClassString = "Button button-auxiliary ico__arrow-first";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_first_Internalname, "", "", bttBtn_first_Jsonclick, 5, "", "", StyleString, ClassString, bttBtn_first_Visible, 1, "standard", "'"+""+"'"+",false,"+"'"+"EFIRST."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 23,'',false,'',0)\"";
         ClassString = "Button button-auxiliary ico__arrow-prev";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_previous_Internalname, "", "", bttBtn_previous_Jsonclick, 5, "", "", StyleString, ClassString, bttBtn_previous_Visible, 1, "standard", "'"+""+"'"+",false,"+"'"+"EPREVIOUS."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 25,'',false,'',0)\"";
         ClassString = "Button button-auxiliary ico__arrow-next";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_next_Internalname, "", "", bttBtn_next_Jsonclick, 5, "", "", StyleString, ClassString, bttBtn_next_Visible, 1, "standard", "'"+""+"'"+",false,"+"'"+"ENEXT."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 27,'',false,'',0)\"";
         ClassString = "Button button-auxiliary ico__arrow-last";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_last_Internalname, "", "", bttBtn_last_Jsonclick, 5, "", "", StyleString, ClassString, bttBtn_last_Visible, 1, "standard", "'"+""+"'"+",false,"+"'"+"ELAST."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 29,'',false,'',0)\"";
         ClassString = "Button button-secondary";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_select_Internalname, "", "Select", bttBtn_select_Jsonclick, 4, "Select", "", StyleString, ClassString, bttBtn_select_Visible, 1, "standard", "'"+""+"'"+",false,"+"'"+"ESELECT."+"'", TempTags, "gx.popup.openPrompt('"+"gx0080.aspx"+"',["+"{Ctrl:gx.dom.el('"+"LIVEMOVEATTRIBUTERULES2ID"+"'), id:'"+"LIVEMOVEATTRIBUTERULES2ID"+"'"+",IOType:'out',isKey:true,isLastKey:true}"+"],"+"null"+","+"'', false"+","+"true"+");"+"return false;", 2, "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12 form__cell-advanced", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "form-group gx-form-group", "start", "top", ""+" data-gx-for=\""+edtLiveMoveAttributeRules2Id_Internalname+"\"", "", "div");
         /* Attribute/Variable Label */
         GxWebStd.gx_label_element( context, edtLiveMoveAttributeRules2Id_Internalname, "Rules2 Id", "col-sm-3 AttributeLabel", 1, true, "");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-sm-9 gx-attribute", "start", "top", "", "", "div");
         /* Single line edit */
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 34,'',false,'',0)\"";
         GxWebStd.gx_single_line_edit( context, edtLiveMoveAttributeRules2Id_Internalname, StringUtil.LTrim( StringUtil.NToC( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0, ".", "")), StringUtil.LTrim( ((edtLiveMoveAttributeRules2Id_Enabled!=0) ? context.localUtil.Format( (decimal)(A33LiveMoveAttributeRules2Id), "ZZZ9") : context.localUtil.Format( (decimal)(A33LiveMoveAttributeRules2Id), "ZZZ9"))), " dir=\"ltr\" inputmode=\"numeric\" pattern=\"[0-9]*\""+TempTags+" onchange=\""+"gx.num.valid_integer( this,',');"+";gx.evt.onchange(this, event)\" "+" onblur=\""+"gx.num.valid_integer( this,',');"+";gx.evt.onblur(this,34);\"", "'"+""+"'"+",false,"+"'"+""+"'", "", "", "", "", edtLiveMoveAttributeRules2Id_Jsonclick, 0, "Attribute", "", "", "", "", 1, edtLiveMoveAttributeRules2Id_Enabled, 0, "text", "1", 4, "chr", 1, "row", 4, 0, 0, 0, 0, -1, 0, true, "", "end", false, "", "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12 form__cell", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "form-group gx-form-group", "start", "top", ""+" data-gx-for=\""+edtLiveMoveAttributeRules2Descrip_Internalname+"\"", "", "div");
         /* Attribute/Variable Label */
         GxWebStd.gx_label_element( context, edtLiveMoveAttributeRules2Descrip_Internalname, "Rules2 Description", "col-sm-3 AttributeLabel", 1, true, "");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-sm-9 gx-attribute", "start", "top", "", "", "div");
         /* Single line edit */
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 39,'',false,'',0)\"";
         GxWebStd.gx_single_line_edit( context, edtLiveMoveAttributeRules2Descrip_Internalname, StringUtil.RTrim( A34LiveMoveAttributeRules2Descrip), StringUtil.RTrim( context.localUtil.Format( A34LiveMoveAttributeRules2Descrip, "")), TempTags+" onchange=\""+""+";gx.evt.onchange(this, event)\" "+" onblur=\""+""+";gx.evt.onblur(this,39);\"", "'"+""+"'"+",false,"+"'"+""+"'", "", "", "", "", edtLiveMoveAttributeRules2Descrip_Jsonclick, 0, "Attribute", "", "", "", "", 1, edtLiveMoveAttributeRules2Descrip_Enabled, 0, "text", "", 20, "chr", 1, "row", 20, 0, 0, 0, 0, -1, -1, true, "", "start", true, "", "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12 form__cell", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "form-group gx-form-group", "start", "top", ""+" data-gx-for=\""+edtLiveMoveAttributeRules2Code_Internalname+"\"", "", "div");
         /* Attribute/Variable Label */
         GxWebStd.gx_label_element( context, edtLiveMoveAttributeRules2Code_Internalname, "Rules2 Code", "col-sm-3 AttributeLabel", 1, true, "");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-sm-9 gx-attribute", "start", "top", "", "", "div");
         /* Single line edit */
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 44,'',false,'',0)\"";
         GxWebStd.gx_single_line_edit( context, edtLiveMoveAttributeRules2Code_Internalname, StringUtil.RTrim( A35LiveMoveAttributeRules2Code), StringUtil.RTrim( context.localUtil.Format( A35LiveMoveAttributeRules2Code, "")), TempTags+" onchange=\""+""+";gx.evt.onchange(this, event)\" "+" onblur=\""+""+";gx.evt.onblur(this,44);\"", "'"+""+"'"+",false,"+"'"+""+"'", "", "", "", "", edtLiveMoveAttributeRules2Code_Jsonclick, 0, "Attribute", "", "", "", "", 1, edtLiveMoveAttributeRules2Code_Enabled, 0, "text", "", 20, "chr", 1, "row", 20, 0, 0, 0, 0, -1, -1, true, "", "start", true, "", "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "row", "start", "top", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "col-xs-12 form__actions--fixed", "end", "Middle", "", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-action-group", "start", "top", " "+"data-gx-actiongroup-type=\"toolbar\""+" ", "", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 49,'',false,'',0)\"";
         ClassString = "Button button-primary";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_enter_Internalname, "", "Confirm", bttBtn_enter_Jsonclick, 5, "Confirm", "", StyleString, ClassString, bttBtn_enter_Visible, bttBtn_enter_Enabled, "standard", "'"+""+"'"+",false,"+"'"+"EENTER."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 51,'',false,'',0)\"";
         ClassString = "Button button-tertiary";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_cancel_Internalname, "", "Cancel", bttBtn_cancel_Jsonclick, 1, "Cancel", "", StyleString, ClassString, bttBtn_cancel_Visible, 1, "standard", "'"+""+"'"+",false,"+"'"+"ECANCEL."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         /* Div Control */
         GxWebStd.gx_div_start( context, "", 1, 0, "px", 0, "px", "gx-button", "start", "top", "", "", "div");
         TempTags = "  onfocus=\"gx.evt.onfocus(this, 53,'',false,'',0)\"";
         ClassString = "Button button-tertiary";
         StyleString = "";
         GxWebStd.gx_button_ctrl( context, bttBtn_delete_Internalname, "", "Delete", bttBtn_delete_Jsonclick, 5, "Delete", "", StyleString, ClassString, bttBtn_delete_Visible, bttBtn_delete_Enabled, "standard", "'"+""+"'"+",false,"+"'"+"EDELETE."+"'", TempTags, "", context.GetButtonType( ), "HLP_LiveMoveAttributeRules2.htm");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "end", "Middle", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
         GxWebStd.gx_div_end( context, "start", "top", "div");
      }

      protected void UserMain( )
      {
         standaloneStartup( ) ;
      }

      protected void UserMainFullajax( )
      {
         INITENV( ) ;
         INITTRN( ) ;
         UserMain( ) ;
         Draw( ) ;
         SendCloseFormHiddens( ) ;
      }

      protected void standaloneStartup( )
      {
         standaloneStartupServer( ) ;
         disable_std_buttons( ) ;
         enableDisable( ) ;
         Process( ) ;
      }

      protected void standaloneStartupServer( )
      {
         context.wbGlbDoneStart = 1;
         assign_properties_default( ) ;
         if ( StringUtil.StrCmp(context.GetRequestMethod( ), "POST") == 0 )
         {
            /* Read saved SDTs. */
            /* Read saved values. */
            Z33LiveMoveAttributeRules2Id = (short)(Math.Round(context.localUtil.CToN( cgiGet( "Z33LiveMoveAttributeRules2Id"), ".", ","), 18, MidpointRounding.ToEven));
            Z35LiveMoveAttributeRules2Code = cgiGet( "Z35LiveMoveAttributeRules2Code");
            Z34LiveMoveAttributeRules2Descrip = cgiGet( "Z34LiveMoveAttributeRules2Descrip");
            IsConfirmed = (short)(Math.Round(context.localUtil.CToN( cgiGet( "IsConfirmed"), ".", ","), 18, MidpointRounding.ToEven));
            IsModified = (short)(Math.Round(context.localUtil.CToN( cgiGet( "IsModified"), ".", ","), 18, MidpointRounding.ToEven));
            Gx_mode = cgiGet( "Mode");
            /* Read variables values. */
            if ( ( ( context.localUtil.CToN( cgiGet( edtLiveMoveAttributeRules2Id_Internalname), ".", ",") < Convert.ToDecimal( 0 )) ) || ( ( context.localUtil.CToN( cgiGet( edtLiveMoveAttributeRules2Id_Internalname), ".", ",") > Convert.ToDecimal( 9999 )) ) )
            {
               GX_msglist.addItem(context.GetMessage( "GXM_badnum", ""), 1, "LIVEMOVEATTRIBUTERULES2ID");
               AnyError = 1;
               GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
               AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
               wbErr = true;
               A33LiveMoveAttributeRules2Id = 0;
               AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
            }
            else
            {
               A33LiveMoveAttributeRules2Id = (short)(Math.Round(context.localUtil.CToN( cgiGet( edtLiveMoveAttributeRules2Id_Internalname), ".", ","), 18, MidpointRounding.ToEven));
               AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
            }
            A34LiveMoveAttributeRules2Descrip = cgiGet( edtLiveMoveAttributeRules2Descrip_Internalname);
            AssignAttri("", false, "A34LiveMoveAttributeRules2Descrip", A34LiveMoveAttributeRules2Descrip);
            A35LiveMoveAttributeRules2Code = cgiGet( edtLiveMoveAttributeRules2Code_Internalname);
            AssignAttri("", false, "A35LiveMoveAttributeRules2Code", A35LiveMoveAttributeRules2Code);
            /* Read subfile selected row values. */
            /* Read hidden variables. */
            GXKey = Decrypt64( context.GetCookie( "GX_SESSION_ID"), Crypto.GetServerKey( ));
            standaloneNotModal( ) ;
         }
         else
         {
            standaloneNotModal( ) ;
            if ( StringUtil.StrCmp(gxfirstwebparm, "viewer") == 0 )
            {
               Gx_mode = "DSP";
               AssignAttri("", false, "Gx_mode", Gx_mode);
               A33LiveMoveAttributeRules2Id = (short)(Math.Round(NumberUtil.Val( GetPar( "LiveMoveAttributeRules2Id"), "."), 18, MidpointRounding.ToEven));
               AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
               getEqualNoModal( ) ;
               Gx_mode = "DSP";
               AssignAttri("", false, "Gx_mode", Gx_mode);
               disable_std_buttons_dsp( ) ;
               standaloneModal( ) ;
            }
            else
            {
               Gx_mode = "INS";
               AssignAttri("", false, "Gx_mode", Gx_mode);
               standaloneModal( ) ;
            }
         }
      }

      protected void Process( )
      {
         if ( StringUtil.StrCmp(context.GetRequestMethod( ), "POST") == 0 )
         {
            /* Read Transaction buttons. */
            sEvt = cgiGet( "_EventName");
            EvtGridId = cgiGet( "_EventGridId");
            EvtRowId = cgiGet( "_EventRowId");
            if ( StringUtil.Len( sEvt) > 0 )
            {
               sEvtType = StringUtil.Left( sEvt, 1);
               sEvt = StringUtil.Right( sEvt, (short)(StringUtil.Len( sEvt)-1));
               if ( StringUtil.StrCmp(sEvtType, "M") != 0 )
               {
                  if ( StringUtil.StrCmp(sEvtType, "E") == 0 )
                  {
                     sEvtType = StringUtil.Right( sEvt, 1);
                     if ( StringUtil.StrCmp(sEvtType, ".") == 0 )
                     {
                        sEvt = StringUtil.Left( sEvt, (short)(StringUtil.Len( sEvt)-1));
                        if ( StringUtil.StrCmp(sEvt, "ENTER") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_enter( ) ;
                           /* No code required for Cancel button. It is implemented as the Reset button. */
                        }
                        else if ( StringUtil.StrCmp(sEvt, "FIRST") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_first( ) ;
                        }
                        else if ( StringUtil.StrCmp(sEvt, "PREVIOUS") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_previous( ) ;
                        }
                        else if ( StringUtil.StrCmp(sEvt, "NEXT") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_next( ) ;
                        }
                        else if ( StringUtil.StrCmp(sEvt, "LAST") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_last( ) ;
                        }
                        else if ( StringUtil.StrCmp(sEvt, "SELECT") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_select( ) ;
                        }
                        else if ( StringUtil.StrCmp(sEvt, "DELETE") == 0 )
                        {
                           context.wbHandled = 1;
                           btn_delete( ) ;
                        }
                        else if ( StringUtil.StrCmp(sEvt, "LSCR") == 0 )
                        {
                           context.wbHandled = 1;
                           AfterKeyLoadScreen( ) ;
                        }
                     }
                     else
                     {
                     }
                  }
                  context.wbHandled = 1;
               }
            }
         }
      }

      protected void AfterTrn( )
      {
         if ( trnEnded == 1 )
         {
            if ( ! String.IsNullOrEmpty(StringUtil.RTrim( endTrnMsgTxt)) )
            {
               GX_msglist.addItem(endTrnMsgTxt, endTrnMsgCod, 0, "", true);
            }
            trnEnded = 0;
            standaloneNotModal( ) ;
            standaloneModal( ) ;
            if ( IsIns( )  )
            {
               /* Clear variables for new insertion. */
               InitAll098( ) ;
               standaloneNotModal( ) ;
               standaloneModal( ) ;
            }
         }
         endTrnMsgTxt = "";
      }

      public override string ToString( )
      {
         return "" ;
      }

      public GxContentInfo GetContentInfo( )
      {
         return (GxContentInfo)(null) ;
      }

      protected void disable_std_buttons( )
      {
         if ( IsIns( ) )
         {
            bttBtn_delete_Enabled = 0;
            AssignProp("", false, bttBtn_delete_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_delete_Enabled), 5, 0), true);
         }
      }

      protected void disable_std_buttons_dsp( )
      {
         bttBtn_delete_Visible = 0;
         AssignProp("", false, bttBtn_delete_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_delete_Visible), 5, 0), true);
         bttBtn_first_Visible = 0;
         AssignProp("", false, bttBtn_first_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_first_Visible), 5, 0), true);
         bttBtn_previous_Visible = 0;
         AssignProp("", false, bttBtn_previous_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_previous_Visible), 5, 0), true);
         bttBtn_next_Visible = 0;
         AssignProp("", false, bttBtn_next_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_next_Visible), 5, 0), true);
         bttBtn_last_Visible = 0;
         AssignProp("", false, bttBtn_last_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_last_Visible), 5, 0), true);
         bttBtn_select_Visible = 0;
         AssignProp("", false, bttBtn_select_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_select_Visible), 5, 0), true);
         bttBtn_delete_Visible = 0;
         AssignProp("", false, bttBtn_delete_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_delete_Visible), 5, 0), true);
         if ( IsDsp( ) )
         {
            bttBtn_enter_Visible = 0;
            AssignProp("", false, bttBtn_enter_Internalname, "Visible", StringUtil.LTrimStr( (decimal)(bttBtn_enter_Visible), 5, 0), true);
         }
         DisableAttributes098( ) ;
      }

      protected void set_caption( )
      {
         if ( ( IsConfirmed == 1 ) && ( AnyError == 0 ) )
         {
            if ( IsDlt( ) )
            {
               GX_msglist.addItem(context.GetMessage( "GXM_confdelete", ""), 0, "", true);
            }
            else
            {
               GX_msglist.addItem(context.GetMessage( "GXM_mustconfirm", ""), 0, "", true);
            }
         }
      }

      protected void ResetCaption090( )
      {
      }

      protected void ZM098( short GX_JID )
      {
         if ( ( GX_JID == 1 ) || ( GX_JID == 0 ) )
         {
            if ( ! IsIns( ) )
            {
               Z35LiveMoveAttributeRules2Code = T00093_A35LiveMoveAttributeRules2Code[0];
               Z34LiveMoveAttributeRules2Descrip = T00093_A34LiveMoveAttributeRules2Descrip[0];
            }
            else
            {
               Z35LiveMoveAttributeRules2Code = A35LiveMoveAttributeRules2Code;
               Z34LiveMoveAttributeRules2Descrip = A34LiveMoveAttributeRules2Descrip;
            }
         }
         if ( GX_JID == -1 )
         {
            Z33LiveMoveAttributeRules2Id = A33LiveMoveAttributeRules2Id;
            Z35LiveMoveAttributeRules2Code = A35LiveMoveAttributeRules2Code;
            Z34LiveMoveAttributeRules2Descrip = A34LiveMoveAttributeRules2Descrip;
         }
      }

      protected void standaloneNotModal( )
      {
      }

      protected void standaloneModal( )
      {
         if ( StringUtil.StrCmp(Gx_mode, "INS") == 0 )
         {
            bttBtn_delete_Enabled = 0;
            AssignProp("", false, bttBtn_delete_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_delete_Enabled), 5, 0), true);
         }
         else
         {
            bttBtn_delete_Enabled = 1;
            AssignProp("", false, bttBtn_delete_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_delete_Enabled), 5, 0), true);
         }
         if ( StringUtil.StrCmp(Gx_mode, "DSP") == 0 )
         {
            bttBtn_enter_Enabled = 0;
            AssignProp("", false, bttBtn_enter_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_enter_Enabled), 5, 0), true);
         }
         else
         {
            bttBtn_enter_Enabled = 1;
            AssignProp("", false, bttBtn_enter_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_enter_Enabled), 5, 0), true);
         }
      }

      protected void Load098( )
      {
         /* Using cursor T00094 */
         pr_default.execute(2, new Object[] {A33LiveMoveAttributeRules2Id});
         if ( (pr_default.getStatus(2) != 101) )
         {
            RcdFound8 = 1;
            A35LiveMoveAttributeRules2Code = T00094_A35LiveMoveAttributeRules2Code[0];
            AssignAttri("", false, "A35LiveMoveAttributeRules2Code", A35LiveMoveAttributeRules2Code);
            A34LiveMoveAttributeRules2Descrip = T00094_A34LiveMoveAttributeRules2Descrip[0];
            AssignAttri("", false, "A34LiveMoveAttributeRules2Descrip", A34LiveMoveAttributeRules2Descrip);
            ZM098( -1) ;
         }
         pr_default.close(2);
         OnLoadActions098( ) ;
      }

      protected void OnLoadActions098( )
      {
      }

      protected void CheckExtendedTable098( )
      {
         Gx_BScreen = 1;
         standaloneModal( ) ;
      }

      protected void CloseExtendedTableCursors098( )
      {
      }

      protected void enableDisable( )
      {
      }

      protected void GetKey098( )
      {
         /* Using cursor T00095 */
         pr_default.execute(3, new Object[] {A33LiveMoveAttributeRules2Id});
         if ( (pr_default.getStatus(3) != 101) )
         {
            RcdFound8 = 1;
         }
         else
         {
            RcdFound8 = 0;
         }
         pr_default.close(3);
      }

      protected void getByPrimaryKey( )
      {
         /* Using cursor T00093 */
         pr_default.execute(1, new Object[] {A33LiveMoveAttributeRules2Id});
         if ( (pr_default.getStatus(1) != 101) )
         {
            ZM098( 1) ;
            RcdFound8 = 1;
            A33LiveMoveAttributeRules2Id = T00093_A33LiveMoveAttributeRules2Id[0];
            AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
            A35LiveMoveAttributeRules2Code = T00093_A35LiveMoveAttributeRules2Code[0];
            AssignAttri("", false, "A35LiveMoveAttributeRules2Code", A35LiveMoveAttributeRules2Code);
            A34LiveMoveAttributeRules2Descrip = T00093_A34LiveMoveAttributeRules2Descrip[0];
            AssignAttri("", false, "A34LiveMoveAttributeRules2Descrip", A34LiveMoveAttributeRules2Descrip);
            Z33LiveMoveAttributeRules2Id = A33LiveMoveAttributeRules2Id;
            sMode8 = Gx_mode;
            Gx_mode = "DSP";
            AssignAttri("", false, "Gx_mode", Gx_mode);
            standaloneModal( ) ;
            Load098( ) ;
            if ( AnyError == 1 )
            {
               RcdFound8 = 0;
               InitializeNonKey098( ) ;
            }
            Gx_mode = sMode8;
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         else
         {
            RcdFound8 = 0;
            InitializeNonKey098( ) ;
            sMode8 = Gx_mode;
            Gx_mode = "DSP";
            AssignAttri("", false, "Gx_mode", Gx_mode);
            standaloneModal( ) ;
            Gx_mode = sMode8;
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         pr_default.close(1);
      }

      protected void getEqualNoModal( )
      {
         GetKey098( ) ;
         if ( RcdFound8 == 0 )
         {
            Gx_mode = "INS";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         else
         {
            Gx_mode = "UPD";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         getByPrimaryKey( ) ;
      }

      protected void move_next( )
      {
         RcdFound8 = 0;
         /* Using cursor T00096 */
         pr_default.execute(4, new Object[] {A33LiveMoveAttributeRules2Id});
         if ( (pr_default.getStatus(4) != 101) )
         {
            while ( (pr_default.getStatus(4) != 101) && ( ( T00096_A33LiveMoveAttributeRules2Id[0] < A33LiveMoveAttributeRules2Id ) ) )
            {
               pr_default.readNext(4);
            }
            if ( (pr_default.getStatus(4) != 101) && ( ( T00096_A33LiveMoveAttributeRules2Id[0] > A33LiveMoveAttributeRules2Id ) ) )
            {
               A33LiveMoveAttributeRules2Id = T00096_A33LiveMoveAttributeRules2Id[0];
               AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
               RcdFound8 = 1;
            }
         }
         pr_default.close(4);
      }

      protected void move_previous( )
      {
         RcdFound8 = 0;
         /* Using cursor T00097 */
         pr_default.execute(5, new Object[] {A33LiveMoveAttributeRules2Id});
         if ( (pr_default.getStatus(5) != 101) )
         {
            while ( (pr_default.getStatus(5) != 101) && ( ( T00097_A33LiveMoveAttributeRules2Id[0] > A33LiveMoveAttributeRules2Id ) ) )
            {
               pr_default.readNext(5);
            }
            if ( (pr_default.getStatus(5) != 101) && ( ( T00097_A33LiveMoveAttributeRules2Id[0] < A33LiveMoveAttributeRules2Id ) ) )
            {
               A33LiveMoveAttributeRules2Id = T00097_A33LiveMoveAttributeRules2Id[0];
               AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
               RcdFound8 = 1;
            }
         }
         pr_default.close(5);
      }

      protected void btn_enter( )
      {
         nKeyPressed = 1;
         GetKey098( ) ;
         if ( IsIns( ) )
         {
            /* Insert record */
            GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
            AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
            Insert098( ) ;
            if ( AnyError == 1 )
            {
               GX_FocusControl = "";
               AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
            }
         }
         else
         {
            if ( RcdFound8 == 1 )
            {
               if ( A33LiveMoveAttributeRules2Id != Z33LiveMoveAttributeRules2Id )
               {
                  A33LiveMoveAttributeRules2Id = Z33LiveMoveAttributeRules2Id;
                  AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
                  GX_msglist.addItem(context.GetMessage( "GXM_getbeforeupd", ""), "CandidateKeyNotFound", 1, "LIVEMOVEATTRIBUTERULES2ID");
                  AnyError = 1;
                  GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
                  AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
               }
               else if ( IsDlt( ) )
               {
                  delete( ) ;
                  AfterTrn( ) ;
                  GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
                  AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
               }
               else
               {
                  Gx_mode = "UPD";
                  AssignAttri("", false, "Gx_mode", Gx_mode);
                  /* Update record */
                  Update098( ) ;
                  GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
                  AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
               }
            }
            else
            {
               if ( A33LiveMoveAttributeRules2Id != Z33LiveMoveAttributeRules2Id )
               {
                  Gx_mode = "INS";
                  AssignAttri("", false, "Gx_mode", Gx_mode);
                  /* Insert record */
                  GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
                  AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
                  Insert098( ) ;
                  if ( AnyError == 1 )
                  {
                     GX_FocusControl = "";
                     AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
                  }
               }
               else
               {
                  if ( StringUtil.StrCmp(Gx_mode, "UPD") == 0 )
                  {
                     GX_msglist.addItem(context.GetMessage( "GXM_recdeleted", ""), 1, "LIVEMOVEATTRIBUTERULES2ID");
                     AnyError = 1;
                     GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
                     AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
                  }
                  else
                  {
                     Gx_mode = "INS";
                     AssignAttri("", false, "Gx_mode", Gx_mode);
                     /* Insert record */
                     GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
                     AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
                     Insert098( ) ;
                     if ( AnyError == 1 )
                     {
                        GX_FocusControl = "";
                        AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
                     }
                  }
               }
            }
         }
         AfterTrn( ) ;
      }

      protected void btn_delete( )
      {
         if ( A33LiveMoveAttributeRules2Id != Z33LiveMoveAttributeRules2Id )
         {
            A33LiveMoveAttributeRules2Id = Z33LiveMoveAttributeRules2Id;
            AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
            GX_msglist.addItem(context.GetMessage( "GXM_getbeforedlt", ""), 1, "LIVEMOVEATTRIBUTERULES2ID");
            AnyError = 1;
            GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
            AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         }
         else
         {
            delete( ) ;
            AfterTrn( ) ;
            GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
            AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         }
         if ( AnyError != 0 )
         {
            Gx_mode = "UPD";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         else
         {
            getByPrimaryKey( ) ;
         }
         CloseCursors();
      }

      protected void btn_get( )
      {
         nKeyPressed = 2;
         IsConfirmed = 0;
         AssignAttri("", false, "IsConfirmed", StringUtil.LTrimStr( (decimal)(IsConfirmed), 4, 0));
         getEqualNoModal( ) ;
         if ( RcdFound8 == 0 )
         {
            GX_msglist.addItem(context.GetMessage( "GXM_keynfound", ""), "PrimaryKeyNotFound", 1, "LIVEMOVEATTRIBUTERULES2ID");
            AnyError = 1;
            GX_FocusControl = edtLiveMoveAttributeRules2Id_Internalname;
            AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         }
         GX_FocusControl = edtLiveMoveAttributeRules2Descrip_Internalname;
         AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         standaloneNotModal( ) ;
         standaloneModal( ) ;
      }

      protected void btn_first( )
      {
         nKeyPressed = 2;
         IsConfirmed = 0;
         AssignAttri("", false, "IsConfirmed", StringUtil.LTrimStr( (decimal)(IsConfirmed), 4, 0));
         ScanStart098( ) ;
         if ( RcdFound8 == 0 )
         {
            GX_msglist.addItem(context.GetMessage( "GXM_norectobrow", ""), 0, "", true);
         }
         else
         {
            Gx_mode = "UPD";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         GX_FocusControl = edtLiveMoveAttributeRules2Descrip_Internalname;
         AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         ScanEnd098( ) ;
         getByPrimaryKey( ) ;
         standaloneNotModal( ) ;
         standaloneModal( ) ;
      }

      protected void btn_previous( )
      {
         nKeyPressed = 2;
         IsConfirmed = 0;
         AssignAttri("", false, "IsConfirmed", StringUtil.LTrimStr( (decimal)(IsConfirmed), 4, 0));
         move_previous( ) ;
         if ( RcdFound8 == 0 )
         {
            GX_msglist.addItem(context.GetMessage( "GXM_norectobrow", ""), 0, "", true);
         }
         else
         {
            Gx_mode = "UPD";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         GX_FocusControl = edtLiveMoveAttributeRules2Descrip_Internalname;
         AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         getByPrimaryKey( ) ;
         standaloneNotModal( ) ;
         standaloneModal( ) ;
      }

      protected void btn_next( )
      {
         nKeyPressed = 2;
         IsConfirmed = 0;
         AssignAttri("", false, "IsConfirmed", StringUtil.LTrimStr( (decimal)(IsConfirmed), 4, 0));
         move_next( ) ;
         if ( RcdFound8 == 0 )
         {
            GX_msglist.addItem(context.GetMessage( "GXM_norectobrow", ""), 0, "", true);
         }
         else
         {
            Gx_mode = "UPD";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         GX_FocusControl = edtLiveMoveAttributeRules2Descrip_Internalname;
         AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         getByPrimaryKey( ) ;
         standaloneNotModal( ) ;
         standaloneModal( ) ;
      }

      protected void btn_last( )
      {
         nKeyPressed = 2;
         IsConfirmed = 0;
         AssignAttri("", false, "IsConfirmed", StringUtil.LTrimStr( (decimal)(IsConfirmed), 4, 0));
         ScanStart098( ) ;
         if ( RcdFound8 == 0 )
         {
            GX_msglist.addItem(context.GetMessage( "GXM_norectobrow", ""), 0, "", true);
         }
         else
         {
            while ( RcdFound8 != 0 )
            {
               ScanNext098( ) ;
            }
            Gx_mode = "UPD";
            AssignAttri("", false, "Gx_mode", Gx_mode);
         }
         GX_FocusControl = edtLiveMoveAttributeRules2Descrip_Internalname;
         AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         ScanEnd098( ) ;
         getByPrimaryKey( ) ;
         standaloneNotModal( ) ;
         standaloneModal( ) ;
      }

      protected void btn_select( )
      {
         getEqualNoModal( ) ;
      }

      protected void CheckOptimisticConcurrency098( )
      {
         if ( ! IsIns( ) )
         {
            /* Using cursor T00092 */
            pr_default.execute(0, new Object[] {A33LiveMoveAttributeRules2Id});
            if ( (pr_default.getStatus(0) == 103) )
            {
               GX_msglist.addItem(context.GetMessage( "GXM_lock", new   object[]  {"LiveMoveAttributeRules2"}), "RecordIsLocked", 1, "");
               AnyError = 1;
               return  ;
            }
            if ( (pr_default.getStatus(0) == 101) || ( StringUtil.StrCmp(Z35LiveMoveAttributeRules2Code, T00092_A35LiveMoveAttributeRules2Code[0]) != 0 ) || ( StringUtil.StrCmp(Z34LiveMoveAttributeRules2Descrip, T00092_A34LiveMoveAttributeRules2Descrip[0]) != 0 ) )
            {
               if ( StringUtil.StrCmp(Z35LiveMoveAttributeRules2Code, T00092_A35LiveMoveAttributeRules2Code[0]) != 0 )
               {
                  GXUtil.WriteLog("livemoveattributerules2:[seudo value changed for attri]"+"LiveMoveAttributeRules2Code");
                  GXUtil.WriteLogRaw("Old: ",Z35LiveMoveAttributeRules2Code);
                  GXUtil.WriteLogRaw("Current: ",T00092_A35LiveMoveAttributeRules2Code[0]);
               }
               if ( StringUtil.StrCmp(Z34LiveMoveAttributeRules2Descrip, T00092_A34LiveMoveAttributeRules2Descrip[0]) != 0 )
               {
                  GXUtil.WriteLog("livemoveattributerules2:[seudo value changed for attri]"+"LiveMoveAttributeRules2Descrip");
                  GXUtil.WriteLogRaw("Old: ",Z34LiveMoveAttributeRules2Descrip);
                  GXUtil.WriteLogRaw("Current: ",T00092_A34LiveMoveAttributeRules2Descrip[0]);
               }
               GX_msglist.addItem(context.GetMessage( "GXM_waschg", new   object[]  {"LiveMoveAttributeRules2"}), "RecordWasChanged", 1, "");
               AnyError = 1;
               return  ;
            }
         }
      }

      protected void Insert098( )
      {
         BeforeValidate098( ) ;
         if ( AnyError == 0 )
         {
            CheckExtendedTable098( ) ;
         }
         if ( AnyError == 0 )
         {
            ZM098( 0) ;
            CheckOptimisticConcurrency098( ) ;
            if ( AnyError == 0 )
            {
               AfterConfirm098( ) ;
               if ( AnyError == 0 )
               {
                  BeforeInsert098( ) ;
                  if ( AnyError == 0 )
                  {
                     /* Using cursor T00098 */
                     pr_default.execute(6, new Object[] {A33LiveMoveAttributeRules2Id, A35LiveMoveAttributeRules2Code, A34LiveMoveAttributeRules2Descrip});
                     pr_default.close(6);
                     pr_default.SmartCacheProvider.SetUpdated("LiveMoveAttributeRules2");
                     if ( (pr_default.getStatus(6) == 1) )
                     {
                        GX_msglist.addItem(context.GetMessage( "GXM_noupdate", ""), "DuplicatePrimaryKey", 1, "");
                        AnyError = 1;
                     }
                     if ( AnyError == 0 )
                     {
                        /* Start of After( Insert) rules */
                        /* End of After( Insert) rules */
                        if ( AnyError == 0 )
                        {
                           /* Save values for previous() function. */
                           endTrnMsgTxt = context.GetMessage( "GXM_sucadded", "");
                           endTrnMsgCod = "SuccessfullyAdded";
                           ResetCaption090( ) ;
                        }
                     }
                  }
                  else
                  {
                     GX_msglist.addItem(context.GetMessage( "GXM_unexp", ""), 1, "");
                     AnyError = 1;
                  }
               }
            }
            else
            {
               Load098( ) ;
            }
            EndLevel098( ) ;
         }
         CloseExtendedTableCursors098( ) ;
      }

      protected void Update098( )
      {
         BeforeValidate098( ) ;
         if ( AnyError == 0 )
         {
            CheckExtendedTable098( ) ;
         }
         if ( AnyError == 0 )
         {
            CheckOptimisticConcurrency098( ) ;
            if ( AnyError == 0 )
            {
               AfterConfirm098( ) ;
               if ( AnyError == 0 )
               {
                  BeforeUpdate098( ) ;
                  if ( AnyError == 0 )
                  {
                     /* Using cursor T00099 */
                     pr_default.execute(7, new Object[] {A35LiveMoveAttributeRules2Code, A34LiveMoveAttributeRules2Descrip, A33LiveMoveAttributeRules2Id});
                     pr_default.close(7);
                     pr_default.SmartCacheProvider.SetUpdated("LiveMoveAttributeRules2");
                     if ( (pr_default.getStatus(7) == 103) )
                     {
                        GX_msglist.addItem(context.GetMessage( "GXM_lock", new   object[]  {"LiveMoveAttributeRules2"}), "RecordIsLocked", 1, "");
                        AnyError = 1;
                     }
                     DeferredUpdate098( ) ;
                     if ( AnyError == 0 )
                     {
                        /* Start of After( update) rules */
                        /* End of After( update) rules */
                        if ( AnyError == 0 )
                        {
                           getByPrimaryKey( ) ;
                           endTrnMsgTxt = context.GetMessage( "GXM_sucupdated", "");
                           endTrnMsgCod = "SuccessfullyUpdated";
                           ResetCaption090( ) ;
                        }
                     }
                     else
                     {
                        GX_msglist.addItem(context.GetMessage( "GXM_unexp", ""), 1, "");
                        AnyError = 1;
                     }
                  }
               }
            }
            EndLevel098( ) ;
         }
         CloseExtendedTableCursors098( ) ;
      }

      protected void DeferredUpdate098( )
      {
      }

      protected void delete( )
      {
         Gx_mode = "DLT";
         AssignAttri("", false, "Gx_mode", Gx_mode);
         BeforeValidate098( ) ;
         if ( AnyError == 0 )
         {
            CheckOptimisticConcurrency098( ) ;
         }
         if ( AnyError == 0 )
         {
            OnDeleteControls098( ) ;
            AfterConfirm098( ) ;
            if ( AnyError == 0 )
            {
               BeforeDelete098( ) ;
               if ( AnyError == 0 )
               {
                  /* No cascading delete specified. */
                  /* Using cursor T000910 */
                  pr_default.execute(8, new Object[] {A33LiveMoveAttributeRules2Id});
                  pr_default.close(8);
                  pr_default.SmartCacheProvider.SetUpdated("LiveMoveAttributeRules2");
                  if ( AnyError == 0 )
                  {
                     /* Start of After( delete) rules */
                     /* End of After( delete) rules */
                     if ( AnyError == 0 )
                     {
                        move_next( ) ;
                        if ( RcdFound8 == 0 )
                        {
                           InitAll098( ) ;
                           Gx_mode = "INS";
                           AssignAttri("", false, "Gx_mode", Gx_mode);
                        }
                        else
                        {
                           getByPrimaryKey( ) ;
                           Gx_mode = "UPD";
                           AssignAttri("", false, "Gx_mode", Gx_mode);
                        }
                        endTrnMsgTxt = context.GetMessage( "GXM_sucdeleted", "");
                        endTrnMsgCod = "SuccessfullyDeleted";
                        ResetCaption090( ) ;
                     }
                  }
                  else
                  {
                     GX_msglist.addItem(context.GetMessage( "GXM_unexp", ""), 1, "");
                     AnyError = 1;
                  }
               }
            }
         }
         sMode8 = Gx_mode;
         Gx_mode = "DLT";
         AssignAttri("", false, "Gx_mode", Gx_mode);
         EndLevel098( ) ;
         Gx_mode = sMode8;
         AssignAttri("", false, "Gx_mode", Gx_mode);
      }

      protected void OnDeleteControls098( )
      {
         standaloneModal( ) ;
         /* No delete mode formulas found. */
      }

      protected void EndLevel098( )
      {
         if ( ! IsIns( ) )
         {
            pr_default.close(0);
         }
         if ( AnyError == 0 )
         {
            BeforeComplete098( ) ;
         }
         if ( AnyError == 0 )
         {
            pr_default.close(1);
            context.CommitDataStores("livemoveattributerules2",pr_default);
            if ( AnyError == 0 )
            {
               ConfirmValues090( ) ;
            }
            /* After transaction rules */
            /* Execute 'After Trn' event if defined. */
            trnEnded = 1;
         }
         else
         {
            pr_default.close(1);
            context.RollbackDataStores("livemoveattributerules2",pr_default);
         }
         IsModified = 0;
         if ( AnyError != 0 )
         {
            context.wjLoc = "";
            context.nUserReturn = 0;
         }
      }

      public void ScanStart098( )
      {
         /* Using cursor T000911 */
         pr_default.execute(9);
         RcdFound8 = 0;
         if ( (pr_default.getStatus(9) != 101) )
         {
            RcdFound8 = 1;
            A33LiveMoveAttributeRules2Id = T000911_A33LiveMoveAttributeRules2Id[0];
            AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
         }
         /* Load Subordinate Levels */
      }

      protected void ScanNext098( )
      {
         /* Scan next routine */
         pr_default.readNext(9);
         RcdFound8 = 0;
         if ( (pr_default.getStatus(9) != 101) )
         {
            RcdFound8 = 1;
            A33LiveMoveAttributeRules2Id = T000911_A33LiveMoveAttributeRules2Id[0];
            AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
         }
      }

      protected void ScanEnd098( )
      {
         pr_default.close(9);
      }

      protected void AfterConfirm098( )
      {
         /* After Confirm Rules */
      }

      protected void BeforeInsert098( )
      {
         /* Before Insert Rules */
      }

      protected void BeforeUpdate098( )
      {
         /* Before Update Rules */
      }

      protected void BeforeDelete098( )
      {
         /* Before Delete Rules */
      }

      protected void BeforeComplete098( )
      {
         /* Before Complete Rules */
      }

      protected void BeforeValidate098( )
      {
         /* Before Validate Rules */
      }

      protected void DisableAttributes098( )
      {
         edtLiveMoveAttributeRules2Id_Enabled = 0;
         AssignProp("", false, edtLiveMoveAttributeRules2Id_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(edtLiveMoveAttributeRules2Id_Enabled), 5, 0), true);
         edtLiveMoveAttributeRules2Descrip_Enabled = 0;
         AssignProp("", false, edtLiveMoveAttributeRules2Descrip_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(edtLiveMoveAttributeRules2Descrip_Enabled), 5, 0), true);
         edtLiveMoveAttributeRules2Code_Enabled = 0;
         AssignProp("", false, edtLiveMoveAttributeRules2Code_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(edtLiveMoveAttributeRules2Code_Enabled), 5, 0), true);
      }

      protected void send_integrity_lvl_hashes098( )
      {
      }

      protected void assign_properties_default( )
      {
      }

      protected void ConfirmValues090( )
      {
      }

      public override void RenderHtmlHeaders( )
      {
         GxWebStd.gx_html_headers( context, 0, "", "", Form.Meta, Form.Metaequiv, true);
      }

      public override void RenderHtmlOpenForm( )
      {
         if ( context.isSpaRequest( ) )
         {
            enableOutput();
         }
         context.WriteHtmlText( "<title>") ;
         context.SendWebValue( Form.Caption) ;
         context.WriteHtmlTextNl( "</title>") ;
         if ( context.isSpaRequest( ) )
         {
            disableOutput();
         }
         if ( StringUtil.Len( sDynURL) > 0 )
         {
            context.WriteHtmlText( "<BASE href=\""+sDynURL+"\" />") ;
         }
         define_styles( ) ;
         MasterPageObj.master_styles();
         CloseStyles();
         if ( ( ( context.GetBrowserType( ) == 1 ) || ( context.GetBrowserType( ) == 5 ) ) && ( StringUtil.StrCmp(context.GetBrowserVersion( ), "7.0") == 0 ) )
         {
            context.AddJavascriptSource("json2.js", "?"+context.GetBuildNumber( 239440), false, true);
         }
         context.AddJavascriptSource("jquery.js", "?"+context.GetBuildNumber( 239440), false, true);
         context.AddJavascriptSource("gxgral.js", "?"+context.GetBuildNumber( 239440), false, true);
         context.AddJavascriptSource("gxcfg.js", "?"+GetCacheInvalidationToken( ), false, true);
         if ( context.isSpaRequest( ) )
         {
            enableOutput();
         }
         context.WriteHtmlText( Form.Headerrawhtml) ;
         context.CloseHtmlHeader();
         if ( context.isSpaRequest( ) )
         {
            disableOutput();
         }
         FormProcess = " data-HasEnter=\"true\" data-Skiponenter=\"false\"";
         context.WriteHtmlText( "<body ") ;
         if ( StringUtil.StrCmp(context.GetLanguageProperty( "rtl"), "true") == 0 )
         {
            context.WriteHtmlText( " dir=\"rtl\" ") ;
         }
         bodyStyle = "" + "background-color:" + context.BuildHTMLColor( Form.Backcolor) + ";color:" + context.BuildHTMLColor( Form.Textcolor) + ";";
         bodyStyle += "-moz-opacity:0;opacity:0;";
         if ( ! ( String.IsNullOrEmpty(StringUtil.RTrim( Form.Background)) ) )
         {
            bodyStyle += " background-image:url(" + context.convertURL( Form.Background) + ")";
         }
         context.WriteHtmlText( " "+"class=\"form-horizontal Form\""+" "+ "style='"+bodyStyle+"'") ;
         context.WriteHtmlText( FormProcess+">") ;
         context.skipLines(1);
         context.WriteHtmlTextNl( "<form id=\"MAINFORM\" autocomplete=\"off\" name=\"MAINFORM\" method=\"post\" tabindex=-1  class=\"form-horizontal Form\" data-gx-class=\"form-horizontal Form\" novalidate action=\""+formatLink("livemoveattributerules2.aspx") +"\">") ;
         GxWebStd.gx_hidden_field( context, "_EventName", "");
         GxWebStd.gx_hidden_field( context, "_EventGridId", "");
         GxWebStd.gx_hidden_field( context, "_EventRowId", "");
         context.WriteHtmlText( "<div style=\"height:0;overflow:hidden\"><input type=\"submit\" title=\"submit\"  disabled></div>") ;
         AssignProp("", false, "FORM", "Class", "form-horizontal Form", true);
         toggleJsOutput = isJsOutputEnabled( );
         if ( context.isSpaRequest( ) )
         {
            disableJsOutput();
         }
      }

      protected void send_integrity_footer_hashes( )
      {
         GXKey = Decrypt64( context.GetCookie( "GX_SESSION_ID"), Crypto.GetServerKey( ));
      }

      protected void SendCloseFormHiddens( )
      {
         /* Send hidden variables. */
         /* Send saved values. */
         send_integrity_footer_hashes( ) ;
         GxWebStd.gx_hidden_field( context, "Z33LiveMoveAttributeRules2Id", StringUtil.LTrim( StringUtil.NToC( (decimal)(Z33LiveMoveAttributeRules2Id), 4, 0, ".", "")));
         GxWebStd.gx_hidden_field( context, "Z35LiveMoveAttributeRules2Code", StringUtil.RTrim( Z35LiveMoveAttributeRules2Code));
         GxWebStd.gx_hidden_field( context, "Z34LiveMoveAttributeRules2Descrip", StringUtil.RTrim( Z34LiveMoveAttributeRules2Descrip));
         GxWebStd.gx_hidden_field( context, "IsConfirmed", StringUtil.LTrim( StringUtil.NToC( (decimal)(IsConfirmed), 4, 0, ".", "")));
         GxWebStd.gx_hidden_field( context, "IsModified", StringUtil.LTrim( StringUtil.NToC( (decimal)(IsModified), 4, 0, ".", "")));
         GxWebStd.gx_hidden_field( context, "Mode", StringUtil.RTrim( Gx_mode));
      }

      public override void RenderHtmlCloseForm( )
      {
         SendCloseFormHiddens( ) ;
         GxWebStd.gx_hidden_field( context, "GX_FocusControl", GX_FocusControl);
         SendAjaxEncryptionKey();
         SendSecurityToken(sPrefix);
         SendComponentObjects();
         SendServerCommands();
         SendState();
         if ( context.isSpaRequest( ) )
         {
            disableOutput();
         }
         context.WriteHtmlTextNl( "</form>") ;
         if ( context.isSpaRequest( ) )
         {
            enableOutput();
         }
         include_jscripts( ) ;
      }

      public override short ExecuteStartEvent( )
      {
         standaloneStartup( ) ;
         gxajaxcallmode = (short)((isAjaxCallMode( ) ? 1 : 0));
         return gxajaxcallmode ;
      }

      public override void RenderHtmlContent( )
      {
         context.WriteHtmlText( "<div") ;
         GxWebStd.ClassAttribute( context, "gx-ct-body"+" "+(String.IsNullOrEmpty(StringUtil.RTrim( Form.Class)) ? "form-horizontal Form" : Form.Class)+"-fx");
         context.WriteHtmlText( ">") ;
         Draw( ) ;
         context.WriteHtmlText( "</div>") ;
      }

      public override void DispatchEvents( )
      {
         Process( ) ;
      }

      public override bool HasEnterEvent( )
      {
         return true ;
      }

      public override GXWebForm GetForm( )
      {
         return Form ;
      }

      public override string GetSelfLink( )
      {
         return formatLink("livemoveattributerules2.aspx")  ;
      }

      public override string GetPgmname( )
      {
         return "LiveMoveAttributeRules2" ;
      }

      public override string GetPgmdesc( )
      {
         return "Live Move Attribute Rules2" ;
      }

      protected void InitializeNonKey098( )
      {
         A35LiveMoveAttributeRules2Code = "";
         AssignAttri("", false, "A35LiveMoveAttributeRules2Code", A35LiveMoveAttributeRules2Code);
         A34LiveMoveAttributeRules2Descrip = "";
         AssignAttri("", false, "A34LiveMoveAttributeRules2Descrip", A34LiveMoveAttributeRules2Descrip);
         Z35LiveMoveAttributeRules2Code = "";
         Z34LiveMoveAttributeRules2Descrip = "";
      }

      protected void InitAll098( )
      {
         A33LiveMoveAttributeRules2Id = 0;
         AssignAttri("", false, "A33LiveMoveAttributeRules2Id", StringUtil.LTrimStr( (decimal)(A33LiveMoveAttributeRules2Id), 4, 0));
         InitializeNonKey098( ) ;
      }

      protected void StandaloneModalInsert( )
      {
      }

      protected void define_styles( )
      {
         AddThemeStyleSheetFile("", context.GetTheme( )+".css", "?"+GetCacheInvalidationToken( ));
         bool outputEnabled = isOutputEnabled( );
         if ( context.isSpaRequest( ) )
         {
            enableOutput();
         }
         idxLst = 1;
         while ( idxLst <= Form.Jscriptsrc.Count )
         {
            context.AddJavascriptSource(StringUtil.RTrim( ((string)Form.Jscriptsrc.Item(idxLst))), "?202610181732295", true, true);
            idxLst = (int)(idxLst+1);
         }
         if ( ! outputEnabled )
         {
            if ( context.isSpaRequest( ) )
            {
               disableOutput();
            }
         }
         /* End function define_styles */
      }

      protected void include_jscripts( )
      {
         context.AddJavascriptSource("messages.eng.js", "?"+GetCacheInvalidationToken( ), false, true);
         context.AddJavascriptSource("livemoveattributerules2.js", "?202610181732295", false, true);
         /* End function include_jscripts */
      }

      protected void init_default_properties( )
      {
         lblTitle_Internalname = "TITLE";
         divTitlecontainer_Internalname = "TITLECONTAINER";
         bttBtn_first_Internalname = "BTN_FIRST";
         bttBtn_previous_Internalname = "BTN_PREVIOUS";
         bttBtn_next_Internalname = "BTN_NEXT";
         bttBtn_last_Internalname = "BTN_LAST";
         bttBtn_select_Internalname = "BTN_SELECT";
         divToolbarcell_Internalname = "TOOLBARCELL";
         edtLiveMoveAttributeRules2Id_Internalname = "LIVEMOVEATTRIBUTERULES2ID";
         edtLiveMoveAttributeRules2Descrip_Internalname = "LIVEMOVEATTRIBUTERULES2DESCRIP";
         edtLiveMoveAttributeRules2Code_Internalname = "LIVEMOVEATTRIBUTERULES2CODE";
         divFormcontainer_Internalname = "FORMCONTAINER";
         bttBtn_enter_Internalname = "BTN_ENTER";
         bttBtn_cancel_Internalname = "BTN_CANCEL";
         bttBtn_delete_Internalname = "BTN_DELETE";
         divMaintable_Internalname = "MAINTABLE";
         Form.Internalname = "FORM";
      }

      public override void initialize_properties( )
      {
         context.SetDefaultTheme("KBTeste", true);
         if ( context.isSpaRequest( ) )
         {
            disableJsOutput();
         }
         init_default_properties( ) ;
         Form.Headerrawhtml = "";
         Form.Background = "";
         Form.Textcolor = 0;
         Form.Backcolor = (int)(0xFFFFFF);
         Form.Caption = "Live Move Attribute Rules2";
         bttBtn_delete_Enabled = 1;
         bttBtn_delete_Visible = 1;
         bttBtn_cancel_Visible = 1;
         bttBtn_enter_Enabled = 1;
         bttBtn_enter_Visible = 1;
         edtLiveMoveAttributeRules2Code_Jsonclick = "";
         edtLiveMoveAttributeRules2Code_Enabled = 1;
         edtLiveMoveAttributeRules2Descrip_Jsonclick = "";
         edtLiveMoveAttributeRules2Descrip_Enabled = 1;
         edtLiveMoveAttributeRules2Id_Jsonclick = "";
         edtLiveMoveAttributeRules2Id_Enabled = 1;
         bttBtn_select_Visible = 1;
         bttBtn_last_Visible = 1;
         bttBtn_next_Visible = 1;
         bttBtn_previous_Visible = 1;
         bttBtn_first_Visible = 1;
         context.GX_msglist.DisplayMode = 1;
         if ( context.isSpaRequest( ) )
         {
            enableJsOutput();
         }
      }

      protected void dynload_actions( )
      {
         /* End function dynload_actions */
      }

      protected void init_web_controls( )
      {
         /* End function init_web_controls */
      }

      protected void AfterKeyLoadScreen( )
      {
         IsConfirmed = 0;
         AssignAttri("", false, "IsConfirmed", StringUtil.LTrimStr( (decimal)(IsConfirmed), 4, 0));
         getEqualNoModal( ) ;
         GX_FocusControl = edtLiveMoveAttributeRules2Descrip_Internalname;
         AssignAttri("", false, "GX_FocusControl", GX_FocusControl);
         standaloneNotModal( ) ;
         standaloneModal( ) ;
         /* End function AfterKeyLoadScreen */
      }

      protected bool IsIns( )
      {
         return ((StringUtil.StrCmp(Gx_mode, "INS")==0) ? true : false) ;
      }

      protected bool IsDlt( )
      {
         return ((StringUtil.StrCmp(Gx_mode, "DLT")==0) ? true : false) ;
      }

      protected bool IsUpd( )
      {
         return ((StringUtil.StrCmp(Gx_mode, "UPD")==0) ? true : false) ;
      }

      protected bool IsDsp( )
      {
         return ((StringUtil.StrCmp(Gx_mode, "DSP")==0) ? true : false) ;
      }

      public void Valid_Livemoveattributerules2id( )
      {
         context.wbHandled = 1;
         AfterKeyLoadScreen( ) ;
         Draw( ) ;
         send_integrity_footer_hashes( ) ;
         dynload_actions( ) ;
         /*  Sending validation outputs */
         AssignAttri("", false, "A35LiveMoveAttributeRules2Code", StringUtil.RTrim( A35LiveMoveAttributeRules2Code));
         AssignAttri("", false, "A34LiveMoveAttributeRules2Descrip", StringUtil.RTrim( A34LiveMoveAttributeRules2Descrip));
         AssignAttri("", false, "Gx_mode", StringUtil.RTrim( Gx_mode));
         GxWebStd.gx_hidden_field( context, "Z33LiveMoveAttributeRules2Id", StringUtil.LTrim( StringUtil.NToC( (decimal)(Z33LiveMoveAttributeRules2Id), 4, 0, ".", "")));
         GxWebStd.gx_hidden_field( context, "Z35LiveMoveAttributeRules2Code", StringUtil.RTrim( Z35LiveMoveAttributeRules2Code));
         GxWebStd.gx_hidden_field( context, "Z34LiveMoveAttributeRules2Descrip", StringUtil.RTrim( Z34LiveMoveAttributeRules2Descrip));
         AssignProp("", false, bttBtn_delete_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_delete_Enabled), 5, 0), true);
         AssignProp("", false, bttBtn_enter_Internalname, "Enabled", StringUtil.LTrimStr( (decimal)(bttBtn_enter_Enabled), 5, 0), true);
         SendCloseFormHiddens( ) ;
      }

      public override bool SupportAjaxEvent( )
      {
         return true ;
      }

      public override void InitializeDynEvents( )
      {
         setEventMetadata("ENTER","""{"handler":"UserMainFullajax","iparms":[{"postForm":true}]}""");
         setEventMetadata("REFRESH","""{"handler":"Refresh","iparms":[]}""");
         setEventMetadata("VALID_LIVEMOVEATTRIBUTERULES2ID","""{"handler":"Valid_Livemoveattributerules2id","iparms":[{"av":"A33LiveMoveAttributeRules2Id","fld":"LIVEMOVEATTRIBUTERULES2ID","pic":"ZZZ9"},{"av":"Gx_mode","fld":"vMODE","pic":"@!"}]""");
         setEventMetadata("VALID_LIVEMOVEATTRIBUTERULES2ID",""","oparms":[{"av":"A35LiveMoveAttributeRules2Code","fld":"LIVEMOVEATTRIBUTERULES2CODE"},{"av":"A34LiveMoveAttributeRules2Descrip","fld":"LIVEMOVEATTRIBUTERULES2DESCRIP"},{"av":"Gx_mode","fld":"vMODE","pic":"@!"},{"av":"Z33LiveMoveAttributeRules2Id"},{"av":"Z35LiveMoveAttributeRules2Code"},{"av":"Z34LiveMoveAttributeRules2Descrip"},{"ctrl":"BTN_DELETE","prop":"Enabled"},{"ctrl":"BTN_ENTER","prop":"Enabled"}]}""");
         return  ;
      }

      public override void cleanup( )
      {
         CloseCursors();
         if ( IsMain )
         {
            context.CloseConnections();
         }
      }

      protected override void CloseCursors( )
      {
         pr_default.close(1);
      }

      public override void initialize( )
      {
         sPrefix = "";
         Z35LiveMoveAttributeRules2Code = "";
         Z34LiveMoveAttributeRules2Descrip = "";
         gxfirstwebparm = "";
         gxfirstwebparm_bkp = "";
         GXKey = "";
         PreviousTooltip = "";
         PreviousCaption = "";
         Form = new GXWebForm();
         GX_FocusControl = "";
         lblTitle_Jsonclick = "";
         ClassString = "";
         StyleString = "";
         TempTags = "";
         bttBtn_first_Jsonclick = "";
         bttBtn_previous_Jsonclick = "";
         bttBtn_next_Jsonclick = "";
         bttBtn_last_Jsonclick = "";
         bttBtn_select_Jsonclick = "";
         A34LiveMoveAttributeRules2Descrip = "";
         A35LiveMoveAttributeRules2Code = "";
         bttBtn_enter_Jsonclick = "";
         bttBtn_cancel_Jsonclick = "";
         bttBtn_delete_Jsonclick = "";
         Gx_mode = "";
         sEvt = "";
         EvtGridId = "";
         EvtRowId = "";
         sEvtType = "";
         endTrnMsgTxt = "";
         endTrnMsgCod = "";
         T00094_A33LiveMoveAttributeRules2Id = new short[1] ;
         T00094_A35LiveMoveAttributeRules2Code = new string[] {""} ;
         T00094_A34LiveMoveAttributeRules2Descrip = new string[] {""} ;
         T00095_A33LiveMoveAttributeRules2Id = new short[1] ;
         T00093_A33LiveMoveAttributeRules2Id = new short[1] ;
         T00093_A35LiveMoveAttributeRules2Code = new string[] {""} ;
         T00093_A34LiveMoveAttributeRules2Descrip = new string[] {""} ;
         sMode8 = "";
         T00096_A33LiveMoveAttributeRules2Id = new short[1] ;
         T00097_A33LiveMoveAttributeRules2Id = new short[1] ;
         T00092_A33LiveMoveAttributeRules2Id = new short[1] ;
         T00092_A35LiveMoveAttributeRules2Code = new string[] {""} ;
         T00092_A34LiveMoveAttributeRules2Descrip = new string[] {""} ;
         T000911_A33LiveMoveAttributeRules2Id = new short[1] ;
         sDynURL = "";
         FormProcess = "";
         bodyStyle = "";
         ZZ35LiveMoveAttributeRules2Code = "";
         ZZ34LiveMoveAttributeRules2Descrip = "";
         pr_default = new DataStoreProvider(context, new GeneXus.Programs.livemoveattributerules2__default(),
            new Object[][] {
                new Object[] {
               T00092_A33LiveMoveAttributeRules2Id, T00092_A35LiveMoveAttributeRules2Code, T00092_A34LiveMoveAttributeRules2Descrip
               }
               , new Object[] {
               T00093_A33LiveMoveAttributeRules2Id, T00093_A35LiveMoveAttributeRules2Code, T00093_A34LiveMoveAttributeRules2Descrip
               }
               , new Object[] {
               T00094_A33LiveMoveAttributeRules2Id, T00094_A35LiveMoveAttributeRules2Code, T00094_A34LiveMoveAttributeRules2Descrip
               }
               , new Object[] {
               T00095_A33LiveMoveAttributeRules2Id
               }
               , new Object[] {
               T00096_A33LiveMoveAttributeRules2Id
               }
               , new Object[] {
               T00097_A33LiveMoveAttributeRules2Id
               }
               , new Object[] {
               }
               , new Object[] {
               }
               , new Object[] {
               }
               , new Object[] {
               T000911_A33LiveMoveAttributeRules2Id
               }
            }
         );
      }

      private short Z33LiveMoveAttributeRules2Id ;
      private short GxWebError ;
      private short gxcookieaux ;
      private short AnyError ;
      private short IsModified ;
      private short IsConfirmed ;
      private short nKeyPressed ;
      private short A33LiveMoveAttributeRules2Id ;
      private short RcdFound8 ;
      private short Gx_BScreen ;
      private short gxajaxcallmode ;
      private short ZZ33LiveMoveAttributeRules2Id ;
      private int trnEnded ;
      private int bttBtn_first_Visible ;
      private int bttBtn_previous_Visible ;
      private int bttBtn_next_Visible ;
      private int bttBtn_last_Visible ;
      private int bttBtn_select_Visible ;
      private int edtLiveMoveAttributeRules2Id_Enabled ;
      private int edtLiveMoveAttributeRules2Descrip_Enabled ;
      private int edtLiveMoveAttributeRules2Code_Enabled ;
      private int bttBtn_enter_Visible ;
      private int bttBtn_enter_Enabled ;
      private int bttBtn_cancel_Visible ;
      private int bttBtn_delete_Visible ;
      private int bttBtn_delete_Enabled ;
      private int idxLst ;
      private string sPrefix ;
      private string Z35LiveMoveAttributeRules2Code ;
      private string Z34LiveMoveAttributeRules2Descrip ;
      private string gxfirstwebparm ;
      private string gxfirstwebparm_bkp ;
      private string GXKey ;
      private string PreviousTooltip ;
      private string PreviousCaption ;
      private string GX_FocusControl ;
      private string edtLiveMoveAttributeRules2Id_Internalname ;
      private string divMaintable_Internalname ;
      private string divTitlecontainer_Internalname ;
      private string lblTitle_Internalname ;
      private string lblTitle_Jsonclick ;
      private string ClassString ;
      private string StyleString ;
      private string divFormcontainer_Internalname ;
      private string divToolbarcell_Internalname ;
      private string TempTags ;
      private string bttBtn_first_Internalname ;
      private string bttBtn_first_Jsonclick ;
      private string bttBtn_previous_Internalname ;
      private string bttBtn_previous_Jsonclick ;
      private string bttBtn_next_Internalname ;
      private string bttBtn_next_Jsonclick ;
      private string bttBtn_last_Internalname ;
      private string bttBtn_last_Jsonclick ;
      private string bttBtn_select_Internalname ;
      private string bttBtn_select_Jsonclick ;
      private string edtLiveMoveAttributeRules2Id_Jsonclick ;
      private string edtLiveMoveAttributeRules2Descrip_Internalname ;
      private string A34LiveMoveAttributeRules2Descrip ;
      private string edtLiveMoveAttributeRules2Descrip_Jsonclick ;
      private string edtLiveMoveAttributeRules2Code_Internalname ;
      private string A35LiveMoveAttributeRules2Code ;
      private string edtLiveMoveAttributeRules2Code_Jsonclick ;
      private string bttBtn_enter_Internalname ;
      private string bttBtn_enter_Jsonclick ;
      private string bttBtn_cancel_Internalname ;
      private string bttBtn_cancel_Jsonclick ;
      private string bttBtn_delete_Internalname ;
      private string bttBtn_delete_Jsonclick ;
      private string Gx_mode ;
      private string sEvt ;
      private string EvtGridId ;
      private string EvtRowId ;
      private string sEvtType ;
      private string endTrnMsgTxt ;
      private string endTrnMsgCod ;
      private string sMode8 ;
      private string sDynURL ;
      private string FormProcess ;
      private string bodyStyle ;
      private string ZZ35LiveMoveAttributeRules2Code ;
      private string ZZ34LiveMoveAttributeRules2Descrip ;
      private bool entryPointCalled ;
      private bool toggleJsOutput ;
      private bool wbErr ;
      private GXWebForm Form ;
      private IDataStoreProvider pr_default ;
      private short[] T00094_A33LiveMoveAttributeRules2Id ;
      private string[] T00094_A35LiveMoveAttributeRules2Code ;
      private string[] T00094_A34LiveMoveAttributeRules2Descrip ;
      private short[] T00095_A33LiveMoveAttributeRules2Id ;
      private short[] T00093_A33LiveMoveAttributeRules2Id ;
      private string[] T00093_A35LiveMoveAttributeRules2Code ;
      private string[] T00093_A34LiveMoveAttributeRules2Descrip ;
      private short[] T00096_A33LiveMoveAttributeRules2Id ;
      private short[] T00097_A33LiveMoveAttributeRules2Id ;
      private short[] T00092_A33LiveMoveAttributeRules2Id ;
      private string[] T00092_A35LiveMoveAttributeRules2Code ;
      private string[] T00092_A34LiveMoveAttributeRules2Descrip ;
      private short[] T000911_A33LiveMoveAttributeRules2Id ;
   }

   public class livemoveattributerules2__default : DataStoreHelperBase, IDataStoreHelper
   {
      public ICursor[] getCursors( )
      {
         cursorDefinitions();
         return new Cursor[] {
          new ForEachCursor(def[0])
         ,new ForEachCursor(def[1])
         ,new ForEachCursor(def[2])
         ,new ForEachCursor(def[3])
         ,new ForEachCursor(def[4])
         ,new ForEachCursor(def[5])
         ,new UpdateCursor(def[6])
         ,new UpdateCursor(def[7])
         ,new UpdateCursor(def[8])
         ,new ForEachCursor(def[9])
       };
    }

    private static CursorDef[] def;
    private void cursorDefinitions( )
    {
       if ( def == null )
       {
          Object[] prmT00092;
          prmT00092 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT00093;
          prmT00093 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT00094;
          prmT00094 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT00095;
          prmT00095 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT00096;
          prmT00096 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT00097;
          prmT00097 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT00098;
          prmT00098 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0) ,
          new ParDef("@LiveMoveAttributeRules2Code",GXType.NChar,20,0) ,
          new ParDef("@LiveMoveAttributeRules2Descrip",GXType.NChar,20,0)
          };
          Object[] prmT00099;
          prmT00099 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Code",GXType.NChar,20,0) ,
          new ParDef("@LiveMoveAttributeRules2Descrip",GXType.NChar,20,0) ,
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT000910;
          prmT000910 = new Object[] {
          new ParDef("@LiveMoveAttributeRules2Id",GXType.Int16,4,0)
          };
          Object[] prmT000911;
          prmT000911 = new Object[] {
          };
          def= new CursorDef[] {
              new CursorDef("T00092", "SELECT [LiveMoveAttributeRules2Id], [LiveMoveAttributeRules2Code], [LiveMoveAttributeRules2Descrip] FROM [LiveMoveAttributeRules2] WITH (UPDLOCK) WHERE [LiveMoveAttributeRules2Id] = @LiveMoveAttributeRules2Id ",true, GxErrorMask.GX_NOMASK, false, this,prmT00092,1, GxCacheFrequency.OFF ,true,false )
             ,new CursorDef("T00093", "SELECT [LiveMoveAttributeRules2Id], [LiveMoveAttributeRules2Code], [LiveMoveAttributeRules2Descrip] FROM [LiveMoveAttributeRules2] WHERE [LiveMoveAttributeRules2Id] = @LiveMoveAttributeRules2Id ",true, GxErrorMask.GX_NOMASK, false, this,prmT00093,1, GxCacheFrequency.OFF ,true,false )
             ,new CursorDef("T00094", "SELECT TM1.[LiveMoveAttributeRules2Id], TM1.[LiveMoveAttributeRules2Code], TM1.[LiveMoveAttributeRules2Descrip] FROM [LiveMoveAttributeRules2] TM1 WHERE TM1.[LiveMoveAttributeRules2Id] = @LiveMoveAttributeRules2Id ORDER BY TM1.[LiveMoveAttributeRules2Id]  OPTION (FAST 100)",true, GxErrorMask.GX_NOMASK, false, this,prmT00094,100, GxCacheFrequency.OFF ,true,false )
             ,new CursorDef("T00095", "SELECT [LiveMoveAttributeRules2Id] FROM [LiveMoveAttributeRules2] WHERE [LiveMoveAttributeRules2Id] = @LiveMoveAttributeRules2Id  OPTION (FAST 1)",true, GxErrorMask.GX_NOMASK, false, this,prmT00095,1, GxCacheFrequency.OFF ,true,false )
             ,new CursorDef("T00096", "SELECT TOP 1 [LiveMoveAttributeRules2Id] FROM [LiveMoveAttributeRules2] WHERE ( [LiveMoveAttributeRules2Id] > @LiveMoveAttributeRules2Id) ORDER BY [LiveMoveAttributeRules2Id]  OPTION (FAST 1)",true, GxErrorMask.GX_NOMASK, false, this,prmT00096,1, GxCacheFrequency.OFF ,true,true )
             ,new CursorDef("T00097", "SELECT TOP 1 [LiveMoveAttributeRules2Id] FROM [LiveMoveAttributeRules2] WHERE ( [LiveMoveAttributeRules2Id] < @LiveMoveAttributeRules2Id) ORDER BY [LiveMoveAttributeRules2Id] DESC  OPTION (FAST 1)",true, GxErrorMask.GX_NOMASK, false, this,prmT00097,1, GxCacheFrequency.OFF ,true,true )
             ,new CursorDef("T00098", "INSERT INTO [LiveMoveAttributeRules2]([LiveMoveAttributeRules2Id], [LiveMoveAttributeRules2Code], [LiveMoveAttributeRules2Descrip]) VALUES(@LiveMoveAttributeRules2Id, @LiveMoveAttributeRules2Code, @LiveMoveAttributeRules2Descrip)", GxErrorMask.GX_NOMASK,prmT00098)
             ,new CursorDef("T00099", "UPDATE [LiveMoveAttributeRules2] SET [LiveMoveAttributeRules2Code]=@LiveMoveAttributeRules2Code, [LiveMoveAttributeRules2Descrip]=@LiveMoveAttributeRules2Descrip  WHERE [LiveMoveAttributeRules2Id] = @LiveMoveAttributeRules2Id", GxErrorMask.GX_NOMASK,prmT00099)
             ,new CursorDef("T000910", "DELETE FROM [LiveMoveAttributeRules2]  WHERE [LiveMoveAttributeRules2Id] = @LiveMoveAttributeRules2Id", GxErrorMask.GX_NOMASK,prmT000910)
             ,new CursorDef("T000911", "SELECT [LiveMoveAttributeRules2Id] FROM [LiveMoveAttributeRules2] ORDER BY [LiveMoveAttributeRules2Id]  OPTION (FAST 100)",true, GxErrorMask.GX_NOMASK, false, this,prmT000911,100, GxCacheFrequency.OFF ,true,false )
          };
       }
    }

    public void getResults( int cursor ,
                            IFieldGetter rslt ,
                            Object[] buf )
    {
       switch ( cursor )
       {
             case 0 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                ((string[]) buf[1])[0] = rslt.getString(2, 20);
                ((string[]) buf[2])[0] = rslt.getString(3, 20);
                return;
             case 1 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                ((string[]) buf[1])[0] = rslt.getString(2, 20);
                ((string[]) buf[2])[0] = rslt.getString(3, 20);
                return;
             case 2 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                ((string[]) buf[1])[0] = rslt.getString(2, 20);
                ((string[]) buf[2])[0] = rslt.getString(3, 20);
                return;
             case 3 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                return;
             case 4 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                return;
             case 5 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                return;
             case 9 :
                ((short[]) buf[0])[0] = rslt.getShort(1);
                return;
       }
    }

 }

}
