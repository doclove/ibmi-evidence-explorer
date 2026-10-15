      * Synthetic decoys; none of these may become facts. FIXTURE-CANARY-FIXED-DECOY
     F* FCOMMENTED IF   E           K DISK
     C*                  CALL      'COMMENTED'
     C                   EVAL      MSG = 'CALL ''INSTRING'''
     C                   CALL      PGMVAR
     C                   CALL      'lowercase'
     C                   CALL      '*LIBL/PGMX'
     C     KEY           CHAIN     NOTDECL
     C                     CALL    'SHIFTED'
     FSHIFTFILE IF   E          K  DISK
     FCUSTMASTIF  E           K        DISK
     FORDFILE   IF   E           K DISK
     F                                     EXTFILE('OTHERLIB/ORDFILE')
     FFLATFILE  IF   F             DISK
      /COPY QRPGLESRC,COPYBOOK
      /FREE
        callp FREEPGM();
        chain key CUSTMAS;
      /END-FREE
     C                   CALL      'BADEND
