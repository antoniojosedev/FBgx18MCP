gx.evt.autoSkip = false;
gx.define('gx0040', false, function () {
   this.ServerClass =  "gx0040" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "gx0040.aspx" ;
   this.setObjectType("web");
   this.anyGridBaseTable = true;
   this.hasEnterEvent = true;
   this.skipOnEnter = false;
   this.autoRefresh = true;
   this.fullAjax = true;
   this.supportAjaxEvents =  true ;
   this.ajaxSecurityToken =  true ;
   this.DSO =  "KBTeste" ;
   this.SetStandaloneVars=function()
   {
      this.AV7pPlan070TrnId=gx.fn.getIntegerValue("vPPLAN070TRNID",',') ;
   };
   this.e122o1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("ADVANCEDCONTAINER","Class") , "AdvancedContainer" ) == 0 )
         {
            gx.fn.setCtrlProperty("ADVANCEDCONTAINER","Class", "AdvancedContainer"+" "+"AdvancedContainerVisible" );
            gx.fn.setCtrlProperty("BTNTOGGLE","Class", gx.fn.getCtrlProperty("BTNTOGGLE","Class")+" "+"BtnToggleActive" );
         }
         else
         {
            gx.fn.setCtrlProperty("ADVANCEDCONTAINER","Class", "AdvancedContainer" );
            gx.fn.setCtrlProperty("BTNTOGGLE","Class", "BtnToggle" );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e112o1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("PLAN070TRNIDFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("PLAN070TRNIDFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCPLAN070TRNID","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("PLAN070TRNIDFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCPLAN070TRNID","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027PLAN070TRNIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"PLAN070TRNIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCPLAN070TRNID\u0027,\u0027Visible\u0027)","ctrl":"vCPLAN070TRNID","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e152o2_client=function()
   {
      return this.executeServerEvent("ENTER", true, arguments[0], false, false);
   };
   this.e162o1_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,25,26,27,28,29];
   this.GXLastCtrlId =29;
   this.Grid1Container = new gx.grid.grid(this, 2,"WbpLvl2",24,"Grid1","Grid1","Grid1Container",this.CmpContext,this.IsMasterPage,"gx0040",[],false,1,false,true,10,true,false,false,"",0,"px",0,"px","New row",true,false,false,null,null,false,"",false,[1,1,1,1],false,0,true,false);
   var Grid1Container = this.Grid1Container;
   Grid1Container.addBitmap("&Linkselection","vLINKSELECTION",25,0,"px",17,"px",null,"","","SelectionAttribute","WWActionColumn");
   Grid1Container.addSingleLineEdit(11,26,"PLAN070TRNID","Trn Id","","Plan070TrnId","int",0,"px",4,4,"end",null,[],11,"Plan070TrnId",true,0,false,false,"Attribute",0,"WWColumn");
   this.Grid1Container.emptyText = "";
   this.setGrid(Grid1Container);
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAIN",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"ADVANCEDCONTAINER",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"PLAN070TRNIDFILTERCONTAINER",grid:0};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[12]={ id: 12, fld:"LBLPLAN070TRNIDFILTER", format:1,grid:0,evt:"e112o1_client", ctrltype: "textblock"};
   GXValidFnc[13]={ id: 13, fld:"",grid:0};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"",grid:0};
   GXValidFnc[16]={ id:16 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCPLAN070TRNID",fmt:0,gxz:"ZV6cPlan070TrnId",gxold:"OV6cPlan070TrnId",gxvar:"AV6cPlan070TrnId",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV6cPlan070TrnId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.ZV6cPlan070TrnId=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("vCPLAN070TRNID",gx.O.AV6cPlan070TrnId,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV6cPlan070TrnId=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("vCPLAN070TRNID",',')},nac:gx.falseFn};
   GXValidFnc[17]={ id: 17, fld:"",grid:0};
   GXValidFnc[18]={ id: 18, fld:"GRIDTABLE",grid:0};
   GXValidFnc[19]={ id: 19, fld:"",grid:0};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"BTNTOGGLE",grid:0,evt:"e122o1_client"};
   GXValidFnc[22]={ id: 22, fld:"",grid:0};
   GXValidFnc[23]={ id: 23, fld:"",grid:0};
   GXValidFnc[25]={ id:25 ,lvl:2,type:"bits",len:1024,dec:0,sign:false,ro:1,isacc:0,grid:24,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"vLINKSELECTION",fmt:0,gxz:"ZV5LinkSelection",gxold:"OV5LinkSelection",gxvar:"AV5LinkSelection",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.AV5LinkSelection=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV5LinkSelection=Value},v2c:function(row){gx.fn.setGridMultimediaValue("vLINKSELECTION",row || gx.fn.currentGridRowImpl(24),gx.O.AV5LinkSelection,gx.O.AV9Linkselection_GXI)},c2v:function(row){gx.O.AV9Linkselection_GXI=this.val_GXI();if(this.val(row)!==undefined)gx.O.AV5LinkSelection=this.val(row)},val:function(row){return gx.fn.getGridControlValue("vLINKSELECTION",row || gx.fn.currentGridRowImpl(24))},val_GXI:function(row){return gx.fn.getGridControlValue("vLINKSELECTION_GXI",row || gx.fn.currentGridRowImpl(24))}, gxvar_GXI:'AV9Linkselection_GXI',nac:gx.falseFn};
   GXValidFnc[26]={ id:26 ,lvl:2,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:1,isacc:0,grid:24,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"PLAN070TRNID",fmt:0,gxz:"Z11Plan070TrnId",gxold:"O11Plan070TrnId",gxvar:"A11Plan070TrnId",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.A11Plan070TrnId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z11Plan070TrnId=gx.num.intval(Value)},v2c:function(row){gx.fn.setGridControlValue("PLAN070TRNID",row || gx.fn.currentGridRowImpl(24),gx.O.A11Plan070TrnId,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A11Plan070TrnId=gx.num.intval(this.val(row))},val:function(row){return gx.fn.getGridIntegerValue("PLAN070TRNID",row || gx.fn.currentGridRowImpl(24),',')},nac:gx.falseFn};
   GXValidFnc[27]={ id: 27, fld:"",grid:0};
   GXValidFnc[28]={ id: 28, fld:"",grid:0};
   GXValidFnc[29]={ id: 29, fld:"BTN_CANCEL",grid:0,evt:"e162o1_client"};
   this.AV6cPlan070TrnId = 0 ;
   this.ZV6cPlan070TrnId = 0 ;
   this.OV6cPlan070TrnId = 0 ;
   this.ZV5LinkSelection = "" ;
   this.OV5LinkSelection = "" ;
   this.Z11Plan070TrnId = 0 ;
   this.O11Plan070TrnId = 0 ;
   this.AV6cPlan070TrnId = 0 ;
   this.AV7pPlan070TrnId = 0 ;
   this.AV5LinkSelection = "" ;
   this.A11Plan070TrnId = 0 ;
   this.Events = {"e152o2_client": ["ENTER", true] ,"e162o1_client": ["CANCEL", true] ,"e122o1_client": ["'TOGGLE'", false] ,"e112o1_client": ["LBLPLAN070TRNIDFILTER.CLICK", false]};
   this.EvtParms["REFRESH"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cPlan070TrnId","fld":"vCPLAN070TRNID","pic":"ZZZ9"}],[]];
   this.EvtParms["'TOGGLE'"] = [[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}]];
   this.EvtParms["LBLPLAN070TRNIDFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027PLAN070TRNIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"PLAN070TRNIDFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027PLAN070TRNIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"PLAN070TRNIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCPLAN070TRNID\u0027,\u0027Visible\u0027)","ctrl":"vCPLAN070TRNID","prop":"Visible"}]];
   this.EvtParms["ENTER"] = [[{"av":"A11Plan070TrnId","fld":"PLAN070TRNID","pic":"ZZZ9","hsh":true}],[{"av":"AV7pPlan070TrnId","fld":"vPPLAN070TRNID","pic":"ZZZ9"}]];
   this.EvtParms["GRID1_FIRSTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cPlan070TrnId","fld":"vCPLAN070TRNID","pic":"ZZZ9"}],[]];
   this.EvtParms["GRID1_PREVPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cPlan070TrnId","fld":"vCPLAN070TRNID","pic":"ZZZ9"}],[]];
   this.EvtParms["GRID1_NEXTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cPlan070TrnId","fld":"vCPLAN070TRNID","pic":"ZZZ9"}],[]];
   this.EvtParms["GRID1_LASTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cPlan070TrnId","fld":"vCPLAN070TRNID","pic":"ZZZ9"}],[]];
   this.setVCMap("AV7pPlan070TrnId", "vPPLAN070TRNID", 0, "int", 4, 0);
   Grid1Container.addRefreshingParm({rfrProp:"Rows", gxGrid:"Grid1"});
   Grid1Container.addRefreshingVar(this.GXValidFnc[16]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[16]);
   this.Initialize( );
});
gx.wi( function() { gx.createParentObj(this.gx0040);});
