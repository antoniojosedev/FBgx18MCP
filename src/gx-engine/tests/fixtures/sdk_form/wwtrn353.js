gx.evt.autoSkip = false;
gx.define('wwtrn353', false, function () {
   this.ServerClass =  "wwtrn353" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "wwtrn353.aspx" ;
   this.setObjectType("web");
   this.anyGridBaseTable = true;
   this.hasEnterEvent = false;
   this.skipOnEnter = false;
   this.autoRefresh = true;
   this.fullAjax = true;
   this.supportAjaxEvents =  true ;
   this.ajaxSecurityToken =  true ;
   this.DSO =  "KBTeste" ;
   this.SetStandaloneVars=function()
   {
   };
   this.e111u2_client=function()
   {
      return this.executeServerEvent("'DOINSERT'", false, null, false, false);
   };
   this.e151u2_client=function()
   {
      return this.executeServerEvent("ENTER", true, arguments[0], false, false);
   };
   this.e161u2_client=function()
   {
      return this.executeServerEvent("CANCEL", true, arguments[0], false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,23,24,26,27,28];
   this.GXLastCtrlId =28;
   this.GridContainer = new gx.grid.grid(this, 2,"WbpLvl2",25,"Grid","Grid","GridContainer",this.CmpContext,this.IsMasterPage,"wwtrn353",[],false,1,false,true,0,true,false,false,"",0,"px",0,"px","New row",true,false,false,null,null,false,"",false,[1,1,1,1],false,0,true,false);
   var GridContainer = this.GridContainer;
   GridContainer.addSingleLineEdit(61,26,"TRN353ID","Id","","Trn353Id","int",0,"px",4,4,"end",null,[],61,"Trn353Id",true,0,false,false,"DescriptionAttribute",0,"WWColumn");
   GridContainer.addSingleLineEdit("Update",27,"vUPDATE","","","Update","char",0,"px",20,20,"start",null,[],"Update","Update",true,0,false,false,"TextActionAttribute TextLikeLink",0,"WWTextActionColumn");
   GridContainer.addSingleLineEdit("Delete",28,"vDELETE","","","Delete","char",0,"px",20,20,"start",null,[],"Delete","Delete",true,0,false,false,"TextActionAttribute TextLikeLink",0,"WWTextActionColumn");
   this.GridContainer.emptyText = "";
   this.setGrid(GridContainer);
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAINTABLE",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"TABLETOP",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"TITLETEXT", format:0,grid:0, ctrltype: "textblock"};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[12]={ id: 12, fld:"",grid:0};
   GXValidFnc[13]={ id: 13, fld:"BTNINSERT",grid:0,evt:"e111u2_client"};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"",grid:0};
   GXValidFnc[16]={ id:16 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:0,grid:0,gxgrid:null,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[this.GridContainer],fld:"vTRN353ID",fmt:0,gxz:"ZV11Trn353Id",gxold:"OV11Trn353Id",gxvar:"AV11Trn353Id",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.AV11Trn353Id=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.ZV11Trn353Id=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("vTRN353ID",gx.O.AV11Trn353Id,0)},c2v:function(){if(this.val()!==undefined)gx.O.AV11Trn353Id=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("vTRN353ID",',')},nac:gx.falseFn};
   GXValidFnc[17]={ id: 17, fld:"",grid:0};
   GXValidFnc[18]={ id: 18, fld:"GRIDCELL",grid:0};
   GXValidFnc[19]={ id: 19, fld:"GRIDTABLE",grid:0};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"",grid:0};
   GXValidFnc[23]={ id: 23, fld:"",grid:0};
   GXValidFnc[24]={ id: 24, fld:"",grid:0};
   GXValidFnc[26]={ id:26 ,lvl:2,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:1,isacc:0,grid:25,gxgrid:this.GridContainer,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"TRN353ID",fmt:0,gxz:"Z61Trn353Id",gxold:"O61Trn353Id",gxvar:"A61Trn353Id",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',v2v:function(Value){if(Value!==undefined)gx.O.A61Trn353Id=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z61Trn353Id=gx.num.intval(Value)},v2c:function(row){gx.fn.setGridControlValue("TRN353ID",row || gx.fn.currentGridRowImpl(25),gx.O.A61Trn353Id,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.A61Trn353Id=gx.num.intval(this.val(row))},val:function(row){return gx.fn.getGridIntegerValue("TRN353ID",row || gx.fn.currentGridRowImpl(25),',')},nac:gx.falseFn};
   GXValidFnc[27]={ id:27 ,lvl:2,type:"char",len:20,dec:0,sign:false,ro:1,isacc:0,grid:25,gxgrid:this.GridContainer,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"vUPDATE",fmt:0,gxz:"ZV12Update",gxold:"OV12Update",gxvar:"AV12Update",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',autoCorrect:"1",v2v:function(Value){if(Value!==undefined)gx.O.AV12Update=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV12Update=Value},v2c:function(row){gx.fn.setGridControlValue("vUPDATE",row || gx.fn.currentGridRowImpl(25),gx.O.AV12Update,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.AV12Update=this.val(row)},val:function(row){return gx.fn.getGridControlValue("vUPDATE",row || gx.fn.currentGridRowImpl(25))},nac:gx.falseFn};
   GXValidFnc[28]={ id:28 ,lvl:2,type:"char",len:20,dec:0,sign:false,ro:1,isacc:0,grid:25,gxgrid:this.GridContainer,fnc:null,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"vDELETE",fmt:0,gxz:"ZV13Delete",gxold:"OV13Delete",gxvar:"AV13Delete",ucs:[],op:[],ip:[],nacdep:[],ctrltype:"edit",inputType:'text',autoCorrect:"1",v2v:function(Value){if(Value!==undefined)gx.O.AV13Delete=Value},v2z:function(Value){if(Value!==undefined)gx.O.ZV13Delete=Value},v2c:function(row){gx.fn.setGridControlValue("vDELETE",row || gx.fn.currentGridRowImpl(25),gx.O.AV13Delete,0)},c2v:function(row){if(this.val(row)!==undefined)gx.O.AV13Delete=this.val(row)},val:function(row){return gx.fn.getGridControlValue("vDELETE",row || gx.fn.currentGridRowImpl(25))},nac:gx.falseFn};
   this.AV11Trn353Id = 0 ;
   this.ZV11Trn353Id = 0 ;
   this.OV11Trn353Id = 0 ;
   this.Z61Trn353Id = 0 ;
   this.O61Trn353Id = 0 ;
   this.ZV12Update = "" ;
   this.OV12Update = "" ;
   this.ZV13Delete = "" ;
   this.OV13Delete = "" ;
   this.AV11Trn353Id = 0 ;
   this.A61Trn353Id = 0 ;
   this.AV12Update = "" ;
   this.AV13Delete = "" ;
   this.Events = {"e111u2_client": ["'DOINSERT'", true] ,"e151u2_client": ["ENTER", true] ,"e161u2_client": ["CANCEL", true]};
   this.EvtParms["REFRESH"] = [[{"av":"GRID_nFirstRecordOnPage"},{"av":"GRID_nEOF"},{"av":"","ctrl":"GRID","prop":"Rows"},{"av":"AV11Trn353Id","fld":"vTRN353ID","pic":"ZZZ9"},{"av":"AV12Update","fld":"vUPDATE"},{"av":"AV13Delete","fld":"vDELETE"}],[]];
   this.EvtParms["GRID.LOAD"] = [[{"av":"A61Trn353Id","fld":"TRN353ID","pic":"ZZZ9","hsh":true}],[{"av":"gx.fn.getCtrlProperty(\u0027vUPDATE\u0027,\u0027Link\u0027)","ctrl":"vUPDATE","prop":"Link"},{"av":"gx.fn.getCtrlProperty(\u0027vDELETE\u0027,\u0027Link\u0027)","ctrl":"vDELETE","prop":"Link"},{"av":"gx.fn.getCtrlProperty(\u0027TRN353ID\u0027,\u0027Link\u0027)","ctrl":"TRN353ID","prop":"Link"}]];
   this.EvtParms["'DOINSERT'"] = [[{"av":"A61Trn353Id","fld":"TRN353ID","pic":"ZZZ9","hsh":true}],[]];
   this.EvtParms["ENTER"] = [[],[]];
   this.EvtParms["GRID_FIRSTPAGE"] = [[{"av":"GRID_nFirstRecordOnPage"},{"av":"GRID_nEOF"},{"av":"","ctrl":"GRID","prop":"Rows"},{"av":"AV11Trn353Id","fld":"vTRN353ID","pic":"ZZZ9"},{"av":"AV12Update","fld":"vUPDATE"},{"av":"AV13Delete","fld":"vDELETE"}],[]];
   this.EvtParms["GRID_PREVPAGE"] = [[{"av":"GRID_nFirstRecordOnPage"},{"av":"GRID_nEOF"},{"av":"","ctrl":"GRID","prop":"Rows"},{"av":"AV11Trn353Id","fld":"vTRN353ID","pic":"ZZZ9"},{"av":"AV12Update","fld":"vUPDATE"},{"av":"AV13Delete","fld":"vDELETE"}],[]];
   this.EvtParms["GRID_NEXTPAGE"] = [[{"av":"GRID_nFirstRecordOnPage"},{"av":"GRID_nEOF"},{"av":"","ctrl":"GRID","prop":"Rows"},{"av":"AV11Trn353Id","fld":"vTRN353ID","pic":"ZZZ9"},{"av":"AV12Update","fld":"vUPDATE"},{"av":"AV13Delete","fld":"vDELETE"}],[]];
   this.EvtParms["GRID_LASTPAGE"] = [[{"av":"GRID_nFirstRecordOnPage"},{"av":"GRID_nEOF"},{"av":"","ctrl":"GRID","prop":"Rows"},{"av":"AV11Trn353Id","fld":"vTRN353ID","pic":"ZZZ9"},{"av":"AV12Update","fld":"vUPDATE"},{"av":"AV13Delete","fld":"vDELETE"}],[]];
   GridContainer.addRefreshingParm({rfrProp:"Rows", gxGrid:"Grid"});
   GridContainer.addRefreshingVar(this.GXValidFnc[16]);
   GridContainer.addRefreshingVar({rfrVar:"AV12Update", rfrProp:"Value", gxAttId:"Update"});
   GridContainer.addRefreshingVar({rfrVar:"AV13Delete", rfrProp:"Value", gxAttId:"Delete"});
   GridContainer.addRefreshingParm(this.GXValidFnc[16]);
   GridContainer.addRefreshingParm({rfrVar:"AV12Update", rfrProp:"Value", gxAttId:"Update"});
   GridContainer.addRefreshingParm({rfrVar:"AV13Delete", rfrProp:"Value", gxAttId:"Delete"});
   this.Initialize( );
});
gx.wi( function() { gx.createParentObj(this.wwtrn353);});
