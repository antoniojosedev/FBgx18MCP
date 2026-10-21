gx.evt.autoSkip = false;
gx.define('gx0010', false, function () {
   this.ServerClass =  "gx0010" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "gx0010.aspx" ;
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
      this.AV9pTrnGroupProbeBaseId=gx.fn.getIntegerValue("vPTRNGROUPPROBEBASEID",',') ;
   };
   this.e142m1_client=function()
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
   this.e112m1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("TRNGROUPPROBEBASEIDFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("TRNGROUPPROBEBASEIDFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCTRNGROUPPROBEBASEID","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("TRNGROUPPROBEBASEIDFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCTRNGROUPPROBEBASEID","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027TRNGROUPPROBEBASEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"TRNGROUPPROBEBASEIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCTRNGROUPPROBEBASEID\u0027,\u0027Visible\u0027)","ctrl":"vCTRNGROUPPROBEBASEID","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e122m1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("GPBASEIDFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("GPBASEIDFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCGPBASEID","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("GPBASEIDFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCGPBASEID","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027GPBASEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"GPBASEIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCGPBASEID\u0027,\u0027Visible\u0027)","ctrl":"vCGPBASEID","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e132m1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("GPBASEDESCFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("GPBASEDESCFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCGPBASEDESC","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("GPBASEDESCFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCGPBASEDESC","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027GPBASEDESCFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"GPBASEDESCFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCGPBASEDESC\u0027,\u0027Visible\u0027)","ctrl":"vCGPBASEDESC","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e172m2_client=function()
   {
      return this.executeServerEvent("ENTER", true, arguments[0], false, false);
   };
   this.e182m1_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,45,46,47,48,49,50,51];
   this.GXLastCtrlId =51;
   this.Grid1Container = new gx.grid.grid(this, 2,"WbpLvl2",44,"Grid1","Grid1","Grid1Container",this.CmpContext,this.IsMasterPage,"gx0010",[],false,1,false,true,10,true,false,false,"",0,"px",0,"px","New row",true,false,false,null,null,false,"",false,[1,1,1,1],false,0,true,false);
   var Grid1Container = this.Grid1Container;
   Grid1Container.addBitmap("&Linkselection","vLINKSELECTION",45,0,"px",17,"px",null,"","","SelectionAttribute","WWActionColumn");
   Grid1Container.addSingleLineEdit(4,46,"TRNGROUPPROBEBASEID","Base Id","","TrnGroupProbeBaseId","int",0,"px",4,4,"end",null,[],4,"TrnGroupProbeBaseId",true,0,false,false,"Attribute",0,"WWColumn");
   Grid1Container.addSingleLineEdit(6,47,"GPBASEID","Base Id","","GpBaseId","int",0,"px",8,8,"end",null,[],6,"GpBaseId",true,0,false,false,"DescriptionAttribute",0,"WWColumn");
   Grid1Container.addSingleLineEdit(7,48,"GPBASEDESC","Base Desc","","GpBaseDesc","svchar",0,"px",40,40,"start",null,[],7,"GpBaseDesc",true,0,false,false,"Attribute",0,"WWColumn OptionalColumn");
   this.Grid1Container.emptyText = "";
   this.setGrid(Grid1Container);
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAIN",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"ADVANCEDCONTAINER",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"TRNGROUPPROBEBASEIDFILTERCONTAINER",grid:0};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[12]={ id: 12, fld:"LBLTRNGROUPPROBEBASEIDFILTER", format:1,grid:0,evt:"e112m1_client", ctrltype: "textblock"};
   GXValidFnc[13]={ id: 13, fld:"",grid:0};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"",grid:0};
   GXValidFnc[16]={ id:16 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCTRNGROUPPROBEBASEID",fmt:0,gxz:"ZV6cTrnGroupProbeBaseId",gxold:"OV6cTrnGroupProbeBaseId",gxvar:"AV6cTrnGroupProbeBaseId",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV6cTrnGroupProbeBaseId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.ZV6cTrnGroupProbeBaseId=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("vCTRNGROUPPROBEBASEID",gx.O.AV6cTrnGroupProbeBaseId,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV6cTrnGroupProbeBaseId=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("vCTRNGROUPPROBEBASEID",',')},nac:gx.falseFn};
   GXValidFnc[17]={ id: 17, fld:"",grid:0};
   GXValidFnc[18]={ id: 18, fld:"",grid:0};
   GXValidFnc[19]={ id: 19, fld:"GPBASEIDFILTERCONTAINER",grid:0};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"",grid:0};
   GXValidFnc[22]={ id: 22, fld:"LBLGPBASEIDFILTER", format:1,grid:0,evt:"e122m1_client", ctrltype: "textblock"};
   GXValidFnc[23]={ id: 23, fld:"",grid:0};
   GXValidFnc[24]={ id: 24, fld:"",grid:0};
   GXValidFnc[25]={ id: 25, fld:"",grid:0};
   GXValidFnc[26]={ id:26 ,lvl:0,type:"int",len:8,dec:0,sign:false,pic:"ZZZZZZZ9",ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCGPBASEID",fmt:0,gxz:"ZV7cGpBaseId",gxold:"OV7cGpBaseId",gxvar:"AV7cGpBaseId",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV7cGpBaseId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.ZV7cGpBaseId=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("vCGPBASEID",gx.O.AV7cGpBaseId,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV7cGpBaseId=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("vCGPBASEID",',')},nac:gx.falseFn};
   GXValidFnc[27]={ id: 27, fld:"",grid:0};
   GXValidFnc[28]={ id: 28, fld:"",grid:0};
   GXValidFnc[29]={ id: 29, fld:"GPBASEDESCFILTERCONTAINER",grid:0};
   GXValidFnc[30]={ id: 30, fld:"",grid:0};
   GXValidFnc[31]={ id: 31, fld:"",grid:0};
   GXValidFnc[32]={ id: 32, fld:"LBLGPBASEDESCFILTER", format:1,grid:0,evt:"e132m1_client", ctrltype: "textblock"};
   GXValidFnc[33]={ id: 33, fld:"",grid:0};
   GXValidFnc[34]={ id: 34, fld:"",grid:0};
   GXValidFnc[35]={ id: 35, fld:"",grid:0};
   GXValidFnc[36]={ id:36 ,lvl:0,type:"svchar",len:40,dec:0,sign:false,ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCGPBASEDESC",fmt:0,gxz:"ZV8cGpBaseDesc",gxold:"OV8cGpBaseDesc",gxvar:"AV8cGpBaseDesc",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV8cGpBaseDesc=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV8cGpBaseDesc=Value},v2c:function(){gx.fn.setControlValue("vCGPBASEDESC",gx.O.AV8cGpBaseDesc,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV8cGpBaseDesc=this.val()},val:function(){return gx.fn.getControlValue("vCGPBASEDESC")},nac:gx.falseFn};
   GXValidFnc[37]={ id: 37, fld:"",grid:0};
   GXValidFnc[38]={ id: 38, fld:"GRIDTABLE",grid:0};
   GXValidFnc[39]={ id: 39, fld:"",grid:0};
   GXValidFnc[40]={ id: 40, fld:"",grid:0};
   GXValidFnc[41]={ id: 41, fld:"BTNTOGGLE",grid:0,evt:"e142m1_client"};
   GXValidFnc[42]={ id: 42, fld:"",grid:0};
   GXValidFnc[43]={ id: 43, fld:"",grid:0};
   GXValidFnc[45]={ id:45 ,lvl:2,type:"bits",len:1024,dec:0,sign:false,ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"vLINKSELECTION",fmt:0,gxz:"ZV5LinkSelection",gxold:"OV5LinkSelection",gxvar:"AV5LinkSelection",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.AV5LinkSelection=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV5LinkSelection=Value},v2c:function(row){gx.fn.setGridMultimediaValue("vLINKSELECTION",row || gx.fn.currentGridRowImpl(44),gx.O.AV5LinkSelection,gx.O.AV11Linkselection_GXI)},c2v:function(row){gx.O.AV11Linkselection_GXI=this.val_GXI();if(this.val(row)!==undefined)gx.O.AV5LinkSelection=this.val(row)},val:function(row){return gx.fn.getGridControlValue("vLINKSELECTION",row || gx.fn.currentGridRowImpl(44))},val_GXI:function(row){return gx.fn.getGridControlValue("vLINKSELECTION_GXI",row || gx.fn.currentGridRowImpl(44))}, gxvar_GXI:'AV11Linkselection_GXI',nac:gx.falseFn};
   GXValidFnc[46]={ id:46 ,lvl:2,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"TRNGROUPPROBEBASEID",fmt:0,gxz:"Z4TrnGroupProbeBaseId",gxold:"O4TrnGroupProbeBaseId",gxvar:"A4TrnGroupProbeBaseId",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.A4TrnGroupProbeBaseId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z4TrnGroupProbeBaseId=gx.num.intval(Value)},v2c:function(row){gx.fn.setGridControlValue("TRNGROUPPROBEBASEID",row || gx.fn.currentGridRowImpl(44),gx.O.A4TrnGroupProbeBaseId,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A4TrnGroupProbeBaseId=gx.num.intval(this.val(row))},val:function(row){return gx.fn.getGridIntegerValue("TRNGROUPPROBEBASEID",row || gx.fn.currentGridRowImpl(44),',')},nac:gx.falseFn};
   GXValidFnc[47]={ id:47 ,lvl:2,type:"int",len:8,dec:0,sign:false,pic:"ZZZZZZZ9",ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"GPBASEID",fmt:0,gxz:"Z6GpBaseId",gxold:"O6GpBaseId",gxvar:"A6GpBaseId",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.A6GpBaseId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z6GpBaseId=gx.num.intval(Value)},v2c:function(row){gx.fn.setGridControlValue("GPBASEID",row || gx.fn.currentGridRowImpl(44),gx.O.A6GpBaseId,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A6GpBaseId=gx.num.intval(this.val(row))},val:function(row){return gx.fn.getGridIntegerValue("GPBASEID",row || gx.fn.currentGridRowImpl(44),',')},nac:gx.falseFn};
   GXValidFnc[48]={ id:48 ,lvl:2,type:"svchar",len:40,dec:0,sign:false,ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"GPBASEDESC",fmt:0,gxz:"Z7GpBaseDesc",gxold:"O7GpBaseDesc",gxvar:"A7GpBaseDesc",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',autoCorrect:"1",v2v:function(Value){if(Value!==undefined)gx.O.A7GpBaseDesc=Value},v2z:function(Value){if(Value!==undefined)gx.O.Z7GpBaseDesc=Value},v2c:function(row){gx.fn.setGridControlValue("GPBASEDESC",row || gx.fn.currentGridRowImpl(44),gx.O.A7GpBaseDesc,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A7GpBaseDesc=this.val(row)},val:function(row){return gx.fn.getGridControlValue("GPBASEDESC",row || gx.fn.currentGridRowImpl(44))},nac:gx.falseFn};
   GXValidFnc[49]={ id: 49, fld:"",grid:0};
   GXValidFnc[50]={ id: 50, fld:"",grid:0};
   GXValidFnc[51]={ id: 51, fld:"BTN_CANCEL",grid:0,evt:"e182m1_client"};
   this.AV6cTrnGroupProbeBaseId = 0 ;
   this.ZV6cTrnGroupProbeBaseId = 0 ;
   this.OV6cTrnGroupProbeBaseId = 0 ;
   this.AV7cGpBaseId = 0 ;
   this.ZV7cGpBaseId = 0 ;
   this.OV7cGpBaseId = 0 ;
   this.AV8cGpBaseDesc = "" ;
   this.ZV8cGpBaseDesc = "" ;
   this.OV8cGpBaseDesc = "" ;
   this.ZV5LinkSelection = "" ;
   this.OV5LinkSelection = "" ;
   this.Z4TrnGroupProbeBaseId = 0 ;
   this.O4TrnGroupProbeBaseId = 0 ;
   this.Z6GpBaseId = 0 ;
   this.O6GpBaseId = 0 ;
   this.Z7GpBaseDesc = "" ;
   this.O7GpBaseDesc = "" ;
   this.AV6cTrnGroupProbeBaseId = 0 ;
   this.AV7cGpBaseId = 0 ;
   this.AV8cGpBaseDesc = "" ;
   this.AV9pTrnGroupProbeBaseId = 0 ;
   this.AV5LinkSelection = "" ;
   this.A4TrnGroupProbeBaseId = 0 ;
   this.A6GpBaseId = 0 ;
   this.A7GpBaseDesc = "" ;
   this.Events = {"e172m2_client": ["ENTER", true] ,"e182m1_client": ["CANCEL", true] ,"e142m1_client": ["'TOGGLE'", false] ,"e112m1_client": ["LBLTRNGROUPPROBEBASEIDFILTER.CLICK", false] ,"e122m1_client": ["LBLGPBASEIDFILTER.CLICK", false] ,"e132m1_client": ["LBLGPBASEDESCFILTER.CLICK", false]};
   this.EvtParms["REFRESH"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cTrnGroupProbeBaseId","fld":"vCTRNGROUPPROBEBASEID","pic":"ZZZ9"},{"av":"AV7cGpBaseId","fld":"vCGPBASEID","pic":"ZZZZZZZ9"},{"av":"AV8cGpBaseDesc","fld":"vCGPBASEDESC"}],[]];
   this.EvtParms["'TOGGLE'"] = [[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}]];
   this.EvtParms["LBLTRNGROUPPROBEBASEIDFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027TRNGROUPPROBEBASEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"TRNGROUPPROBEBASEIDFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027TRNGROUPPROBEBASEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"TRNGROUPPROBEBASEIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCTRNGROUPPROBEBASEID\u0027,\u0027Visible\u0027)","ctrl":"vCTRNGROUPPROBEBASEID","prop":"Visible"}]];
   this.EvtParms["LBLGPBASEIDFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027GPBASEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"GPBASEIDFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027GPBASEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"GPBASEIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCGPBASEID\u0027,\u0027Visible\u0027)","ctrl":"vCGPBASEID","prop":"Visible"}]];
   this.EvtParms["LBLGPBASEDESCFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027GPBASEDESCFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"GPBASEDESCFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027GPBASEDESCFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"GPBASEDESCFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCGPBASEDESC\u0027,\u0027Visible\u0027)","ctrl":"vCGPBASEDESC","prop":"Visible"}]];
   this.EvtParms["ENTER"] = [[{"av":"A4TrnGroupProbeBaseId","fld":"TRNGROUPPROBEBASEID","pic":"ZZZ9","hsh":true}],[{"av":"AV9pTrnGroupProbeBaseId","fld":"vPTRNGROUPPROBEBASEID","pic":"ZZZ9"}]];
   this.EvtParms["GRID1_FIRSTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cTrnGroupProbeBaseId","fld":"vCTRNGROUPPROBEBASEID","pic":"ZZZ9"},{"av":"AV7cGpBaseId","fld":"vCGPBASEID","pic":"ZZZZZZZ9"},{"av":"AV8cGpBaseDesc","fld":"vCGPBASEDESC"}],[]];
   this.EvtParms["GRID1_PREVPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cTrnGroupProbeBaseId","fld":"vCTRNGROUPPROBEBASEID","pic":"ZZZ9"},{"av":"AV7cGpBaseId","fld":"vCGPBASEID","pic":"ZZZZZZZ9"},{"av":"AV8cGpBaseDesc","fld":"vCGPBASEDESC"}],[]];
   this.EvtParms["GRID1_NEXTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cTrnGroupProbeBaseId","fld":"vCTRNGROUPPROBEBASEID","pic":"ZZZ9"},{"av":"AV7cGpBaseId","fld":"vCGPBASEID","pic":"ZZZZZZZ9"},{"av":"AV8cGpBaseDesc","fld":"vCGPBASEDESC"}],[]];
   this.EvtParms["GRID1_LASTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cTrnGroupProbeBaseId","fld":"vCTRNGROUPPROBEBASEID","pic":"ZZZ9"},{"av":"AV7cGpBaseId","fld":"vCGPBASEID","pic":"ZZZZZZZ9"},{"av":"AV8cGpBaseDesc","fld":"vCGPBASEDESC"}],[]];
   this.setVCMap("AV9pTrnGroupProbeBaseId", "vPTRNGROUPPROBEBASEID", 0, "int", 4, 0);
   Grid1Container.addRefreshingParm({rfrProp:"Rows", gxGrid:"Grid1"});
   Grid1Container.addRefreshingVar(this.GXValidFnc[16]);
   Grid1Container.addRefreshingVar(this.GXValidFnc[26]);
   Grid1Container.addRefreshingVar(this.GXValidFnc[36]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[16]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[26]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[36]);
   this.Initialize( );
});
gx.wi( function() { gx.createParentObj(this.gx0010);});
