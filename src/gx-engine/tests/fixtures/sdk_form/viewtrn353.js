gx.evt.autoSkip = false;
gx.define('viewtrn353', false, function () {
   this.ServerClass =  "viewtrn353" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "viewtrn353.aspx" ;
   this.setObjectType("web");
   this.hasEnterEvent = false;
   this.skipOnEnter = false;
   this.autoRefresh = true;
   this.fullAjax = true;
   this.supportAjaxEvents =  true ;
   this.ajaxSecurityToken =  true ;
   this.DSO =  "KBTeste" ;
   this.SetStandaloneVars=function()
   {
      this.AV11LoadAllTabs=gx.fn.getControlValue("vLOADALLTABS") ;
      this.AV7SelectedTabCode=gx.fn.getControlValue("vSELECTEDTABCODE") ;
      this.AV12Trn353Id=gx.fn.getIntegerValue("vTRN353ID",',') ;
      this.AV6TabCode=gx.fn.getControlValue("vTABCODE") ;
   };
   this.s112_client=function()
   {
      return this.executeClientEvent(  function() {
         if ( this.AV11LoadAllTabs || gx.text.compare( this.AV7SelectedTabCode , "" ) == 0 || gx.text.compare( this.AV7SelectedTabCode , "General" ) == 0 )
         {
            this.createWebComponent('Generalwc','Trn353General',[this.AV12Trn353Id]);
         }
      }, arguments);
   };
   this.e131t1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         this.AV7SelectedTabCode =  this.TABContainer.ActivePageControlName  ;
         this.AV11LoadAllTabs =  false  ;
         this.s112_client();
         this.refreshOutputs([{"av":"AV7SelectedTabCode","fld":"vSELECTEDTABCODE"},{"av":"AV11LoadAllTabs","fld":"vLOADALLTABS"},{"ctrl":"GENERALWC"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e141t2_client=function()
   {
      return this.executeServerEvent("ENTER", true, null, false, false);
   };
   this.e151t2_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,24,25,27,28,29];
   this.GXLastCtrlId =29;
   this.TABContainer = gx.uc.getNew(this, 22, 19, "gx.ui.controls.BasicTab", "TABContainer", "Tab", "TAB");
   var TABContainer = this.TABContainer;
   TABContainer.setProp("Enabled", "Enabled", true, "boolean");
   TABContainer.setProp("ActivePage", "Activepage", '', "int");
   TABContainer.setDynProp("ActivePageControlName", "Activepagecontrolname", "", "char");
   TABContainer.setProp("PageCount", "Pagecount", 1, "num");
   TABContainer.setProp("Class", "Class", "WWTab", "str");
   TABContainer.setProp("HistoryManagement", "Historymanagement", true, "bool");
   TABContainer.setProp("Visible", "Visible", true, "bool");
   TABContainer.setC2ShowFunction(function(UC) { UC.show(); });
   TABContainer.addEventHandler("TabChanged", this.e131t1_client);
   this.setUserControl(TABContainer);
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAINTABLE",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"TABLETOP",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"VIEWTITLE", format:0,grid:0, ctrltype: "textblock"};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"VIEWALL", format:0,grid:0, ctrltype: "textblock"};
   GXValidFnc[12]={ id: 12, fld:"",grid:0};
   GXValidFnc[13]={ id: 13, fld:"",grid:0};
   GXValidFnc[14]={ id: 14, fld:"TABTABLE_1",grid:0};
   GXValidFnc[15]={ id: 15, fld:"",grid:0};
   GXValidFnc[16]={ id: 16, fld:"",grid:0};
   GXValidFnc[17]={ id: 17, fld:"",grid:0};
   GXValidFnc[18]={ id: 18, fld:"",grid:0};
   GXValidFnc[19]={ id:19 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:1,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"TRN353ID",fmt:0,gxz:"Z61Trn353Id",gxold:"O61Trn353Id",gxvar:"A61Trn353Id",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.A61Trn353Id=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z61Trn353Id=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("TRN353ID",gx.O.A61Trn353Id,0)},c2v:function(){if(this.val()!==undefined)gx.O.A61Trn353Id=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("TRN353ID",',')},nac:gx.falseFn};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"",grid:0};
   GXValidFnc[24]={ id: 24, fld:"GENERAL_TITLE", format:0,grid:0, ctrltype: "textblock"};
   GXValidFnc[25]={ id: 25, fld:"",grid:0};
   GXValidFnc[27]={ id: 27, fld:"TABLEGENERAL",grid:0};
   GXValidFnc[28]={ id: 28, fld:"",grid:0};
   GXValidFnc[29]={ id: 29, fld:"",grid:0};
   this.A61Trn353Id = 0 ;
   this.Z61Trn353Id = 0 ;
   this.O61Trn353Id = 0 ;
   this.A61Trn353Id = 0 ;
   this.AV12Trn353Id = 0 ;
   this.AV6TabCode = "" ;
   this.AV11LoadAllTabs = false ;
   this.AV7SelectedTabCode = "" ;
   this.Events = {"e141t2_client": ["ENTER", true] ,"e151t2_client": ["CANCEL", true] ,"e131t1_client": ["TAB.TABCHANGED", false]};
   this.EvtParms["REFRESH"] = [[{"av":"AV12Trn353Id","fld":"vTRN353ID","pic":"ZZZ9","hsh":true},{"av":"AV6TabCode","fld":"vTABCODE","hsh":true},{"av":"A61Trn353Id","fld":"TRN353ID","pic":"ZZZ9"}],[]];
   this.EvtParms["TAB.TABCHANGED"] = [[{"av":"this.TABContainer.ActivePageControlName","ctrl":"TAB","prop":"ActivePageControlName"},{"av":"AV11LoadAllTabs","fld":"vLOADALLTABS"},{"av":"AV7SelectedTabCode","fld":"vSELECTEDTABCODE"},{"av":"AV12Trn353Id","fld":"vTRN353ID","pic":"ZZZ9","hsh":true}],[{"av":"AV7SelectedTabCode","fld":"vSELECTEDTABCODE"},{"av":"AV11LoadAllTabs","fld":"vLOADALLTABS"},{"ctrl":"GENERALWC"}]];
   this.EvtParms["ENTER"] = [[],[]];
   this.setVCMap("AV11LoadAllTabs", "vLOADALLTABS", 0, "boolean", 4, 0);
   this.setVCMap("AV7SelectedTabCode", "vSELECTEDTABCODE", 0, "char", 50, 0);
   this.setVCMap("AV12Trn353Id", "vTRN353ID", 0, "int", 4, 0);
   this.setVCMap("AV6TabCode", "vTABCODE", 0, "char", 50, 0);
   this.setVCMap("AV12Trn353Id", "vTRN353ID", 0, "int", 4, 0);
   this.setVCMap("AV11LoadAllTabs", "vLOADALLTABS", 0, "boolean", 4, 0);
   this.setVCMap("AV7SelectedTabCode", "vSELECTEDTABCODE", 0, "char", 50, 0);
   this.Initialize( );
   this.setComponent({id: "GENERALWC" ,GXClass: null , Prefix: "W0030" , lvl: 1 });
});
gx.wi( function() { gx.createParentObj(this.viewtrn353);});
