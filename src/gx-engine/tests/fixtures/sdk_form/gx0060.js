gx.evt.autoSkip = false;
gx.define('gx0060', false, function () {
   this.ServerClass =  "gx0060" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "gx0060.aspx" ;
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
      this.AV9pLiveMoveAttributeId=gx.fn.getIntegerValue("vPLIVEMOVEATTRIBUTEID",',') ;
   };
   this.e142q1_client=function()
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
   this.e112q1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("LIVEMOVEATTRIBUTEIDFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("LIVEMOVEATTRIBUTEIDFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCLIVEMOVEATTRIBUTEID","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("LIVEMOVEATTRIBUTEIDFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCLIVEMOVEATTRIBUTEID","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTEIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCLIVEMOVEATTRIBUTEID\u0027,\u0027Visible\u0027)","ctrl":"vCLIVEMOVEATTRIBUTEID","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e122q1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("LIVEMOVEATTRIBUTECODEFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("LIVEMOVEATTRIBUTECODEFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCLIVEMOVEATTRIBUTECODE","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("LIVEMOVEATTRIBUTECODEFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCLIVEMOVEATTRIBUTECODE","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTECODEFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTECODEFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCLIVEMOVEATTRIBUTECODE\u0027,\u0027Visible\u0027)","ctrl":"vCLIVEMOVEATTRIBUTECODE","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e132q1_client=function()
   {
      return this.executeClientEvent(  function() {
         this.clearMessages();
         if ( gx.text.compare( gx.fn.getCtrlProperty("LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER","Class") , "AdvancedContainerItem" ) == 0 )
         {
            gx.fn.setCtrlProperty("LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER","Class", "AdvancedContainerItem"+" "+"AdvancedContainerItemExpanded" );
            gx.fn.setCtrlProperty("vCLIVEMOVEATTRIBUTEDESCRIPTION","Visible", true );
         }
         else
         {
            gx.fn.setCtrlProperty("LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER","Class", "AdvancedContainerItem" );
            gx.fn.setCtrlProperty("vCLIVEMOVEATTRIBUTEDESCRIPTION","Visible", false );
         }
         this.refreshOutputs([{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCLIVEMOVEATTRIBUTEDESCRIPTION\u0027,\u0027Visible\u0027)","ctrl":"vCLIVEMOVEATTRIBUTEDESCRIPTION","prop":"Visible"}]);
         this.OnClientEventEnd();
      }, arguments);
   };
   this.e172q2_client=function()
   {
      return this.executeServerEvent("ENTER", true, arguments[0], false, false);
   };
   this.e182q1_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,45,46,47,48,49,50,51];
   this.GXLastCtrlId =51;
   this.Grid1Container = new gx.grid.grid(this, 2,"WbpLvl2",44,"Grid1","Grid1","Grid1Container",this.CmpContext,this.IsMasterPage,"gx0060",[],false,1,false,true,10,true,false,false,"",0,"px",0,"px","New row",true,false,false,null,null,false,"",false,[1,1,1,1],false,0,true,false);
   var Grid1Container = this.Grid1Container;
   Grid1Container.addBitmap("&Linkselection","vLINKSELECTION",45,0,"px",17,"px",null,"","","SelectionAttribute","WWActionColumn");
   Grid1Container.addSingleLineEdit(27,46,"LIVEMOVEATTRIBUTEID","Attribute Id","","LiveMoveAttributeId","int",0,"px",4,4,"end",null,[],27,"LiveMoveAttributeId",true,0,false,false,"Attribute",0,"WWColumn");
   Grid1Container.addSingleLineEdit(28,47,"LIVEMOVEATTRIBUTECODE","Attribute Code","","LiveMoveAttributeCode","char",0,"px",20,20,"start",null,[],28,"LiveMoveAttributeCode",true,0,false,false,"DescriptionAttribute",0,"WWColumn");
   Grid1Container.addSingleLineEdit(29,48,"LIVEMOVEATTRIBUTEDESCRIPTION","Attribute Description","","LiveMoveAttributeDescription","char",0,"px",20,20,"start",null,[],29,"LiveMoveAttributeDescription",true,0,false,false,"Attribute",0,"WWColumn OptionalColumn");
   this.Grid1Container.emptyText = "";
   this.setGrid(Grid1Container);
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAIN",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"ADVANCEDCONTAINER",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"LIVEMOVEATTRIBUTEIDFILTERCONTAINER",grid:0};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[12]={ id: 12, fld:"LBLLIVEMOVEATTRIBUTEIDFILTER", format:1,grid:0,evt:"e112q1_client", ctrltype: "textblock"};
   GXValidFnc[13]={ id: 13, fld:"",grid:0};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"",grid:0};
   GXValidFnc[16]={ id:16 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCLIVEMOVEATTRIBUTEID",fmt:0,gxz:"ZV6cLiveMoveAttributeId",gxold:"OV6cLiveMoveAttributeId",gxvar:"AV6cLiveMoveAttributeId",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV6cLiveMoveAttributeId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.ZV6cLiveMoveAttributeId=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("vCLIVEMOVEATTRIBUTEID",gx.O.AV6cLiveMoveAttributeId,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV6cLiveMoveAttributeId=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("vCLIVEMOVEATTRIBUTEID",',')},nac:gx.falseFn};
   GXValidFnc[17]={ id: 17, fld:"",grid:0};
   GXValidFnc[18]={ id: 18, fld:"",grid:0};
   GXValidFnc[19]={ id: 19, fld:"LIVEMOVEATTRIBUTECODEFILTERCONTAINER",grid:0};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"",grid:0};
   GXValidFnc[22]={ id: 22, fld:"LBLLIVEMOVEATTRIBUTECODEFILTER", format:1,grid:0,evt:"e122q1_client", ctrltype: "textblock"};
   GXValidFnc[23]={ id: 23, fld:"",grid:0};
   GXValidFnc[24]={ id: 24, fld:"",grid:0};
   GXValidFnc[25]={ id: 25, fld:"",grid:0};
   GXValidFnc[26]={ id:26 ,lvl:0,type:"char",len:20,dec:0,sign:false,ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCLIVEMOVEATTRIBUTECODE",fmt:0,gxz:"ZV7cLiveMoveAttributeCode",gxold:"OV7cLiveMoveAttributeCode",gxvar:"AV7cLiveMoveAttributeCode",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV7cLiveMoveAttributeCode=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV7cLiveMoveAttributeCode=Value},v2c:function(){gx.fn.setControlValue("vCLIVEMOVEATTRIBUTECODE",gx.O.AV7cLiveMoveAttributeCode,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV7cLiveMoveAttributeCode=this.val()},val:function(){return gx.fn.getControlValue("vCLIVEMOVEATTRIBUTECODE")},nac:gx.falseFn};
   GXValidFnc[27]={ id: 27, fld:"",grid:0};
   GXValidFnc[28]={ id: 28, fld:"",grid:0};
   GXValidFnc[29]={ id: 29, fld:"LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER",grid:0};
   GXValidFnc[30]={ id: 30, fld:"",grid:0};
   GXValidFnc[31]={ id: 31, fld:"",grid:0};
   GXValidFnc[32]={ id: 32, fld:"LBLLIVEMOVEATTRIBUTEDESCRIPTIONFILTER", format:1,grid:0,evt:"e132q1_client", ctrltype: "textblock"};
   GXValidFnc[33]={ id: 33, fld:"",grid:0};
   GXValidFnc[34]={ id: 34, fld:"",grid:0};
   GXValidFnc[35]={ id: 35, fld:"",grid:0};
   GXValidFnc[36]={ id:36 ,lvl:0,type:"char",len:20,dec:0,sign:false,ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.Grid1Container],fld:"vCLIVEMOVEATTRIBUTEDESCRIPTION",fmt:0,gxz:"ZV8cLiveMoveAttributeDescription",gxold:"OV8cLiveMoveAttributeDescription",gxvar:"AV8cLiveMoveAttributeDescription",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV8cLiveMoveAttributeDescription=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV8cLiveMoveAttributeDescription=Value},v2c:function(){gx.fn.setControlValue("vCLIVEMOVEATTRIBUTEDESCRIPTION",gx.O.AV8cLiveMoveAttributeDescription,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV8cLiveMoveAttributeDescription=this.val()},val:function(){return gx.fn.getControlValue("vCLIVEMOVEATTRIBUTEDESCRIPTION")},nac:gx.falseFn};
   GXValidFnc[37]={ id: 37, fld:"",grid:0};
   GXValidFnc[38]={ id: 38, fld:"GRIDTABLE",grid:0};
   GXValidFnc[39]={ id: 39, fld:"",grid:0};
   GXValidFnc[40]={ id: 40, fld:"",grid:0};
   GXValidFnc[41]={ id: 41, fld:"BTNTOGGLE",grid:0,evt:"e142q1_client"};
   GXValidFnc[42]={ id: 42, fld:"",grid:0};
   GXValidFnc[43]={ id: 43, fld:"",grid:0};
   GXValidFnc[45]={ id:45 ,lvl:2,type:"bits",len:1024,dec:0,sign:false,ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"vLINKSELECTION",fmt:0,gxz:"ZV5LinkSelection",gxold:"OV5LinkSelection",gxvar:"AV5LinkSelection",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.AV5LinkSelection=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV5LinkSelection=Value},v2c:function(row){gx.fn.setGridMultimediaValue("vLINKSELECTION",row || gx.fn.currentGridRowImpl(44),gx.O.AV5LinkSelection,gx.O.AV11Linkselection_GXI)},c2v:function(row){gx.O.AV11Linkselection_GXI=this.val_GXI();if(this.val(row)!==undefined)gx.O.AV5LinkSelection=this.val(row)},val:function(row){return gx.fn.getGridControlValue("vLINKSELECTION",row || gx.fn.currentGridRowImpl(44))},val_GXI:function(row){return gx.fn.getGridControlValue("vLINKSELECTION_GXI",row || gx.fn.currentGridRowImpl(44))}, gxvar_GXI:'AV11Linkselection_GXI',nac:gx.falseFn};
   GXValidFnc[46]={ id:46 ,lvl:2,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"LIVEMOVEATTRIBUTEID",fmt:0,gxz:"Z27LiveMoveAttributeId",gxold:"O27LiveMoveAttributeId",gxvar:"A27LiveMoveAttributeId",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.A27LiveMoveAttributeId=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z27LiveMoveAttributeId=gx.num.intval(Value)},v2c:function(row){gx.fn.setGridControlValue("LIVEMOVEATTRIBUTEID",row || gx.fn.currentGridRowImpl(44),gx.O.A27LiveMoveAttributeId,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A27LiveMoveAttributeId=gx.num.intval(this.val(row))},val:function(row){return gx.fn.getGridIntegerValue("LIVEMOVEATTRIBUTEID",row || gx.fn.currentGridRowImpl(44),',')},nac:gx.falseFn};
   GXValidFnc[47]={ id:47 ,lvl:2,type:"char",len:20,dec:0,sign:false,ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"LIVEMOVEATTRIBUTECODE",fmt:0,gxz:"Z28LiveMoveAttributeCode",gxold:"O28LiveMoveAttributeCode",gxvar:"A28LiveMoveAttributeCode",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',autoCorrect:"1",v2v:function(Value){if(Value!==undefined)gx.O.A28LiveMoveAttributeCode=Value},v2z:function(Value){if(Value!==undefined)gx.O.Z28LiveMoveAttributeCode=Value},v2c:function(row){gx.fn.setGridControlValue("LIVEMOVEATTRIBUTECODE",row || gx.fn.currentGridRowImpl(44),gx.O.A28LiveMoveAttributeCode,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A28LiveMoveAttributeCode=this.val(row)},val:function(row){return gx.fn.getGridControlValue("LIVEMOVEATTRIBUTECODE",row || gx.fn.currentGridRowImpl(44))},nac:gx.falseFn};
   GXValidFnc[48]={ id:48 ,lvl:2,type:"char",len:20,dec:0,sign:false,ro:1,isacc:0,grid:44,gxgrid:this.Grid1Container,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"LIVEMOVEATTRIBUTEDESCRIPTION",fmt:0,gxz:"Z29LiveMoveAttributeDescription",gxold:"O29LiveMoveAttributeDescription",gxvar:"A29LiveMoveAttributeDescription",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',autoCorrect:"1",v2v:function(Value){if(Value!==undefined)gx.O.A29LiveMoveAttributeDescription=Value},v2z:function(Value){if(Value!==undefined)gx.O.Z29LiveMoveAttributeDescription=Value},v2c:function(row){gx.fn.setGridControlValue("LIVEMOVEATTRIBUTEDESCRIPTION",row || gx.fn.currentGridRowImpl(44),gx.O.A29LiveMoveAttributeDescription,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A29LiveMoveAttributeDescription=this.val(row)},val:function(row){return gx.fn.getGridControlValue("LIVEMOVEATTRIBUTEDESCRIPTION",row || gx.fn.currentGridRowImpl(44))},nac:gx.falseFn};
   GXValidFnc[49]={ id: 49, fld:"",grid:0};
   GXValidFnc[50]={ id: 50, fld:"",grid:0};
   GXValidFnc[51]={ id: 51, fld:"BTN_CANCEL",grid:0,evt:"e182q1_client"};
   this.AV6cLiveMoveAttributeId = 0 ;
   this.ZV6cLiveMoveAttributeId = 0 ;
   this.OV6cLiveMoveAttributeId = 0 ;
   this.AV7cLiveMoveAttributeCode = "" ;
   this.ZV7cLiveMoveAttributeCode = "" ;
   this.OV7cLiveMoveAttributeCode = "" ;
   this.AV8cLiveMoveAttributeDescription = "" ;
   this.ZV8cLiveMoveAttributeDescription = "" ;
   this.OV8cLiveMoveAttributeDescription = "" ;
   this.ZV5LinkSelection = "" ;
   this.OV5LinkSelection = "" ;
   this.Z27LiveMoveAttributeId = 0 ;
   this.O27LiveMoveAttributeId = 0 ;
   this.Z28LiveMoveAttributeCode = "" ;
   this.O28LiveMoveAttributeCode = "" ;
   this.Z29LiveMoveAttributeDescription = "" ;
   this.O29LiveMoveAttributeDescription = "" ;
   this.AV6cLiveMoveAttributeId = 0 ;
   this.AV7cLiveMoveAttributeCode = "" ;
   this.AV8cLiveMoveAttributeDescription = "" ;
   this.AV9pLiveMoveAttributeId = 0 ;
   this.AV5LinkSelection = "" ;
   this.A27LiveMoveAttributeId = 0 ;
   this.A28LiveMoveAttributeCode = "" ;
   this.A29LiveMoveAttributeDescription = "" ;
   this.Events = {"e172q2_client": ["ENTER", true] ,"e182q1_client": ["CANCEL", true] ,"e142q1_client": ["'TOGGLE'", false] ,"e112q1_client": ["LBLLIVEMOVEATTRIBUTEIDFILTER.CLICK", false] ,"e122q1_client": ["LBLLIVEMOVEATTRIBUTECODEFILTER.CLICK", false] ,"e132q1_client": ["LBLLIVEMOVEATTRIBUTEDESCRIPTIONFILTER.CLICK", false]};
   this.EvtParms["REFRESH"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cLiveMoveAttributeId","fld":"vCLIVEMOVEATTRIBUTEID","pic":"ZZZ9"},{"av":"AV7cLiveMoveAttributeCode","fld":"vCLIVEMOVEATTRIBUTECODE"},{"av":"AV8cLiveMoveAttributeDescription","fld":"vCLIVEMOVEATTRIBUTEDESCRIPTION"}],[]];
   this.EvtParms["'TOGGLE'"] = [[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027ADVANCEDCONTAINER\u0027,\u0027Class\u0027)","ctrl":"ADVANCEDCONTAINER","prop":"Class"},{"ctrl":"BTNTOGGLE","prop":"Class"}]];
   this.EvtParms["LBLLIVEMOVEATTRIBUTEIDFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTEIDFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTEIDFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTEIDFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCLIVEMOVEATTRIBUTEID\u0027,\u0027Visible\u0027)","ctrl":"vCLIVEMOVEATTRIBUTEID","prop":"Visible"}]];
   this.EvtParms["LBLLIVEMOVEATTRIBUTECODEFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTECODEFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTECODEFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTECODEFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTECODEFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCLIVEMOVEATTRIBUTECODE\u0027,\u0027Visible\u0027)","ctrl":"vCLIVEMOVEATTRIBUTECODE","prop":"Visible"}]];
   this.EvtParms["LBLLIVEMOVEATTRIBUTEDESCRIPTIONFILTER.CLICK"] = [[{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER","prop":"Class"}],[{"av":"gx.fn.getCtrlProperty(\u0027LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER\u0027,\u0027Class\u0027)","ctrl":"LIVEMOVEATTRIBUTEDESCRIPTIONFILTERCONTAINER","prop":"Class"},{"av":"gx.fn.getCtrlProperty(\u0027vCLIVEMOVEATTRIBUTEDESCRIPTION\u0027,\u0027Visible\u0027)","ctrl":"vCLIVEMOVEATTRIBUTEDESCRIPTION","prop":"Visible"}]];
   this.EvtParms["ENTER"] = [[{"av":"A27LiveMoveAttributeId","fld":"LIVEMOVEATTRIBUTEID","pic":"ZZZ9","hsh":true}],[{"av":"AV9pLiveMoveAttributeId","fld":"vPLIVEMOVEATTRIBUTEID","pic":"ZZZ9"}]];
   this.EvtParms["GRID1_FIRSTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cLiveMoveAttributeId","fld":"vCLIVEMOVEATTRIBUTEID","pic":"ZZZ9"},{"av":"AV7cLiveMoveAttributeCode","fld":"vCLIVEMOVEATTRIBUTECODE"},{"av":"AV8cLiveMoveAttributeDescription","fld":"vCLIVEMOVEATTRIBUTEDESCRIPTION"}],[]];
   this.EvtParms["GRID1_PREVPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cLiveMoveAttributeId","fld":"vCLIVEMOVEATTRIBUTEID","pic":"ZZZ9"},{"av":"AV7cLiveMoveAttributeCode","fld":"vCLIVEMOVEATTRIBUTECODE"},{"av":"AV8cLiveMoveAttributeDescription","fld":"vCLIVEMOVEATTRIBUTEDESCRIPTION"}],[]];
   this.EvtParms["GRID1_NEXTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cLiveMoveAttributeId","fld":"vCLIVEMOVEATTRIBUTEID","pic":"ZZZ9"},{"av":"AV7cLiveMoveAttributeCode","fld":"vCLIVEMOVEATTRIBUTECODE"},{"av":"AV8cLiveMoveAttributeDescription","fld":"vCLIVEMOVEATTRIBUTEDESCRIPTION"}],[]];
   this.EvtParms["GRID1_LASTPAGE"] = [[{"av":"GRID1_nFirstRecordOnPage"},{"av":"GRID1_nEOF"},{"av":"","ctrl":"GRID1","prop":"Rows"},{"av":"AV6cLiveMoveAttributeId","fld":"vCLIVEMOVEATTRIBUTEID","pic":"ZZZ9"},{"av":"AV7cLiveMoveAttributeCode","fld":"vCLIVEMOVEATTRIBUTECODE"},{"av":"AV8cLiveMoveAttributeDescription","fld":"vCLIVEMOVEATTRIBUTEDESCRIPTION"}],[]];
   this.setVCMap("AV9pLiveMoveAttributeId", "vPLIVEMOVEATTRIBUTEID", 0, "int", 4, 0);
   Grid1Container.addRefreshingParm({rfrProp:"Rows", gxGrid:"Grid1"});
   Grid1Container.addRefreshingVar(this.GXValidFnc[16]);
   Grid1Container.addRefreshingVar(this.GXValidFnc[26]);
   Grid1Container.addRefreshingVar(this.GXValidFnc[36]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[16]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[26]);
   Grid1Container.addRefreshingParm(this.GXValidFnc[36]);
   this.Initialize( );
});
gx.wi( function() { gx.createParentObj(this.gx0060);});
