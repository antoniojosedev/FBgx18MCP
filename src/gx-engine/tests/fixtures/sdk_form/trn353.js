gx.evt.autoSkip = false;
gx.define('trn353', false, function () {
   this.ServerClass =  "trn353" ;
   this.PackageName =  "GeneXus.Programs" ;
   this.ServerFullClass =  "trn353.aspx" ;
   this.setObjectType("trn");
   this.hasEnterEvent = true;
   this.skipOnEnter = false;
   this.fullAjax = true;
   this.supportAjaxEvents =  true ;
   this.ajaxSecurityToken =  true ;
   this.DSO =  "KBTeste" ;
   this.SetStandaloneVars=function()
   {
      this.AV7Trn353Id=gx.fn.getIntegerValue("vTRN353ID",',') ;
      this.AV11Pgmname=gx.fn.getControlValue("vPGMNAME") ;
      this.Gx_mode=gx.fn.getControlValue("vMODE") ;
      this.AV9TrnContext=gx.fn.getControlValue("vTRNCONTEXT") ;
   };
   this.Valid_Trn353id=function()
   {
      return this.validCliEvt("Valid_Trn353id", 0, function () {
      try {
         var gxballoon = gx.util.balloon.getNew("TRN353ID");
         this.AnyError  = 0;

      }
      catch(e){}
      try {
          if (gxballoon == null) return true; return gxballoon.show();
      }
      catch(e){}
      return true ;
      });
   }
   this.e12142_client=function()
   {
      return this.executeServerEvent("AFTER TRN", true, null, false, false);
   };
   this.e13149_client=function()
   {
      return this.executeServerEvent("ENTER", true, null, false, false);
   };
   this.e14149_client=function()
   {
      return this.executeServerEvent("CANCEL", true, null, false, false);
   };
   this.GXValidFnc = [];
   var GXValidFnc = this.GXValidFnc ;
   this.GXCtrlIds=[2,3,4,5,6,7,8,9,10,11,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43];
   this.GXLastCtrlId =43;
   GXValidFnc[2]={ id: 2, fld:"",grid:0};
   GXValidFnc[3]={ id: 3, fld:"MAINTABLE",grid:0};
   GXValidFnc[4]={ id: 4, fld:"",grid:0};
   GXValidFnc[5]={ id: 5, fld:"",grid:0};
   GXValidFnc[6]={ id: 6, fld:"TITLECONTAINER",grid:0};
   GXValidFnc[7]={ id: 7, fld:"",grid:0};
   GXValidFnc[8]={ id: 8, fld:"",grid:0};
   GXValidFnc[9]={ id: 9, fld:"TITLE", format:0,grid:0, ctrltype: "textblock"};
   GXValidFnc[10]={ id: 10, fld:"",grid:0};
   GXValidFnc[11]={ id: 11, fld:"",grid:0};
   GXValidFnc[13]={ id: 13, fld:"",grid:0};
   GXValidFnc[14]={ id: 14, fld:"",grid:0};
   GXValidFnc[15]={ id: 15, fld:"FORMCONTAINER",grid:0};
   GXValidFnc[16]={ id: 16, fld:"",grid:0};
   GXValidFnc[17]={ id: 17, fld:"TOOLBARCELL",grid:0};
   GXValidFnc[18]={ id: 18, fld:"",grid:0};
   GXValidFnc[19]={ id: 19, fld:"",grid:0};
   GXValidFnc[20]={ id: 20, fld:"",grid:0};
   GXValidFnc[21]={ id: 21, fld:"BTN_FIRST",grid:0,evt:"e15149_client",std:"FIRST"};
   GXValidFnc[22]={ id: 22, fld:"",grid:0};
   GXValidFnc[23]={ id: 23, fld:"BTN_PREVIOUS",grid:0,evt:"e16149_client",std:"PREVIOUS"};
   GXValidFnc[24]={ id: 24, fld:"",grid:0};
   GXValidFnc[25]={ id: 25, fld:"BTN_NEXT",grid:0,evt:"e17149_client",std:"NEXT"};
   GXValidFnc[26]={ id: 26, fld:"",grid:0};
   GXValidFnc[27]={ id: 27, fld:"BTN_LAST",grid:0,evt:"e18149_client",std:"LAST"};
   GXValidFnc[28]={ id: 28, fld:"",grid:0};
   GXValidFnc[29]={ id: 29, fld:"BTN_SELECT",grid:0,evt:"e19149_client",std:"SELECT"};
   GXValidFnc[30]={ id: 30, fld:"",grid:0};
   GXValidFnc[31]={ id: 31, fld:"",grid:0};
   GXValidFnc[32]={ id: 32, fld:"",grid:0};
   GXValidFnc[33]={ id: 33, fld:"",grid:0};
   GXValidFnc[34]={ id:34 ,lvl:0,type:"int",len:4,dec:0,sign:false,pic:"ZZZ9",ro:0,grid:0,gxgrid:null,fnc:this.Valid_Trn353id,isvalid:null,evt_cvc:null,evt_cvcing:null,rgrid:[],fld:"TRN353ID",fmt:0,gxz:"Z61Trn353Id",gxold:"O61Trn353Id",gxvar:"A61Trn353Id",ucs:[],op:[],ip:[],
						nacdep:[],ctrltype:"edit",v2v:function(Value){if(Value!==undefined)gx.O.A61Trn353Id=gx.num.intval(Value)},v2z:function(Value){if(Value!==undefined)gx.O.Z61Trn353Id=gx.num.intval(Value)},v2c:function(){gx.fn.setControlValue("TRN353ID",gx.O.A61Trn353Id,0)},c2v:function(){if(this.val()!==undefined)gx.O.A61Trn353Id=gx.num.intval(this.val())},val:function(){return gx.fn.getIntegerValue("TRN353ID",',')},nac:function(){return !((0==this.AV7Trn353Id))}};
   GXValidFnc[35]={ id: 35, fld:"",grid:0};
   GXValidFnc[36]={ id: 36, fld:"",grid:0};
   GXValidFnc[37]={ id: 37, fld:"",grid:0};
   GXValidFnc[38]={ id: 38, fld:"",grid:0};
   GXValidFnc[39]={ id: 39, fld:"BTN_ENTER",grid:0,evt:"e13149_client",std:"ENTER"};
   GXValidFnc[40]={ id: 40, fld:"",grid:0};
   GXValidFnc[41]={ id: 41, fld:"BTN_CANCEL",grid:0,evt:"e14149_client"};
   GXValidFnc[42]={ id: 42, fld:"",grid:0};
   GXValidFnc[43]={ id: 43, fld:"BTN_DELETE",grid:0,evt:"e20149_client",std:"DELETE"};
   this.A61Trn353Id = 0 ;
   this.Z61Trn353Id = 0 ;
   this.O61Trn353Id = 0 ;
   this.AV11Pgmname = "" ;
   this.AV9TrnContext = {CallerObject:"",CallerOnDelete:false,CallerURL:"",TransactionName:"",Attributes:[]} ;
   this.AV7Trn353Id = 0 ;
   this.AV10WebSession = {} ;
   this.A61Trn353Id = 0 ;
   this.Gx_mode = "" ;
   this.Events = {"e12142_client": ["AFTER TRN", true] ,"e13149_client": ["ENTER", true] ,"e14149_client": ["CANCEL", true]};
   this.EvtParms["ENTER"] = [[{"postForm":true},{"av":"Gx_mode","fld":"vMODE","pic":"@!","hsh":true},{"av":"AV7Trn353Id","fld":"vTRN353ID","pic":"ZZZ9","hsh":true}],[]];
   this.EvtParms["REFRESH"] = [[{"av":"Gx_mode","fld":"vMODE","pic":"@!","hsh":true},{"av":"AV9TrnContext","fld":"vTRNCONTEXT","hsh":true},{"av":"AV7Trn353Id","fld":"vTRN353ID","pic":"ZZZ9","hsh":true}],[]];
   this.EvtParms["AFTER TRN"] = [[{"av":"Gx_mode","fld":"vMODE","pic":"@!","hsh":true},{"av":"AV9TrnContext","fld":"vTRNCONTEXT","hsh":true}],[]];
   this.EvtParms["VALID_TRN353ID"] = [[],[]];
   this.EnterCtrl = ["BTN_ENTER"];
   this.setVCMap("AV7Trn353Id", "vTRN353ID", 0, "int", 4, 0);
   this.setVCMap("AV11Pgmname", "vPGMNAME", 0, "char", 129, 0);
   this.setVCMap("Gx_mode", "vMODE", 0, "char", 3, 0);
   this.setVCMap("AV9TrnContext", "vTRNCONTEXT", 0, "General\UI\TransactionContext", 0, 0);
   this.Initialize( );
});
gx.wi( function() { gx.createParentObj(this.trn353);});
