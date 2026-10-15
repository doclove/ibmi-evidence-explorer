     H DFTACTGRP(*NO)
      * Synthetic fixed-form inquiry. FIXTURE-CANARY-FIXED
     FCUSTMAS   IF   E           K DISK
     FCUSTRPT   O    E             PRINTER
     D CUSTID          S              7P 0
     C     CUSTID        CHAIN     CUSTMAS
     C                   CALL      'CUSTUPD'
     C                   PARM                    CUSTID
     C                   SETON                                        LR
