**free
// dcl-f COMMENTED keyed; FIXTURE-CANARY-RPG
ctl-opt main(entry);
dcl-s msg varchar(60) inz('dcl-f INSTRING; chain k INSTRING;');
dcl-f NOSUCHFILE keyed;
dcl-f DUPFILE keyed;
dcl-f ORDFILE extfile('OTHERLIB/ORDFILE');
chain key UNDECLARED;
chain(e) key DUPFILE;
dcl-f ;
chain key DUPFILE;
