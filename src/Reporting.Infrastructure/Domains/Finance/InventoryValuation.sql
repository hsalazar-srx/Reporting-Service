-- =============================================================================
-- INVENTORY VALUATION — WAC HISTORY FIFO LAYER UNWIND
-- Source  : Karen Lewin, "Inventory Valuation 2026Oct13.sql", plus her 2026-10-16 revision
--           (the standard-cost lookup only; marked [SOURCE REVISION]). Methodology otherwise
--           UNCHANGED pending Finance validation (Donna Pullen). Every edit of ours is
--           marked [CHANGED].
-- Tokens  : filled in by InventoryValuationFetcher, in order of appearance —
--   {{SCHEMA}}                  per-company DB2 schema (100 = MVXCDTA, 300 = MVXC300)
--   {{CONO}}                    company, validated digits (was the literal 100)
--   {{WAREHOUSES}}              ?, ?, ... one positional parameter per warehouse
--   {{FILTERS}}                 optional AND IB.MBITNO = ? / AND ITM.MMITGR = ?
--   {{REVALUE_DATE:alias}}      optional AND alias.CUCUTD <= ? (revaluation rate date)
--   {{BOOK_RATE:expression}}    the book-rate CASE, generated from the book rates read
--                               from CCURRA (was repeated seven times with literal rates)
--   {{RATE_DIVISION}}           CCURRA division (was the literal 'D')       - config
--   {{SPOT_RATE_TYPE}}          CCURRA spot rate type (was the literal '99') - config
--   {{ROW_CAP}}                 FETCH FIRST maxRows+1 ROWS ONLY
-- See the original file for the full column and formula commentary.
-- =============================================================================

SELECT
    BASE.WAREHOUSE,
    BASE.ITEM_NO,
    BASE.ITEM_DESCRIPTION,

    -- SOH_QUANTITY shown on first row per item only (cosmetic, as in the original)
    CASE
        WHEN BASE.ITEM_ROW_NUM = 1
            THEN BASE.SOH_QUANTITY
        ELSE NULL
    END                                         AS SOH_QUANTITY,

    CASE
        WHEN BASE.RUNNING_TOTAL <= BASE.SOH_QUANTITY
            THEN BASE.TRANSACTION_QTY
        ELSE BASE.TRANSACTION_QTY
             - (BASE.RUNNING_TOTAL - BASE.SOH_QUANTITY)
    END                                         AS LAYER_QTY,

    CASE
        WHEN COALESCE(BASE.TRN_CURRENCY, '') = 'AUD'
            THEN COALESCE(BASE.ACQUISITION_COST, 0)
        WHEN COALESCE(AVFX.CUARAT, 0) > 0
            THEN COALESCE(BASE.ACQUISITION_COST, 0)
                 * {{BOOK_RATE:BASE.TRN_CURRENCY}}
                 / AVFX.CUARAT
        ELSE COALESCE(BASE.ACQUISITION_COST, 0)
    END                                         AS AUD_NEW_AVG_COST_SPOT_RATE,

    BASE.ACQUISITION_COST,
    BASE.NEW_AVG_COST                           AS AUD_NEW_AVG_COST_BOOK_RATE,
    BASE.TRN_CURRENCY,
    COALESCE(AVFX.CUARAT, 0)                    AS SPOT_RATE_AT_ENTRY_DATE,
    BASE.ORDER_NUMBER,
    BASE.TRANSACTION_QTY,

    CASE
        WHEN COALESCE(BASE.TRN_CURRENCY, '') = 'AUD'
            THEN COALESCE(BASE.LAYER_QTY_CALC, 0)
                 * COALESCE(BASE.ACQUISITION_COST, 0)
        WHEN COALESCE(AVFX.CUARAT, 0) > 0
            THEN COALESCE(BASE.LAYER_QTY_CALC, 0)
                 * COALESCE(BASE.ACQUISITION_COST, 0)
                 * {{BOOK_RATE:BASE.TRN_CURRENCY}}
                 / AVFX.CUARAT
        ELSE COALESCE(BASE.LAYER_QTY_CALC, 0)
             * COALESCE(BASE.ACQUISITION_COST, 0)
    END                                         AS STOCK_VALUE_AT_ACQUISITION_RATE,

    COALESCE(CRFX.CUARAT, 0)                    AS CURRENT_SPOT_RATE,

    CASE
        WHEN COALESCE(BASE.TRN_CURRENCY, '') = 'AUD'
            THEN COALESCE(BASE.LAYER_QTY_CALC, 0)
                 * COALESCE(BASE.ACQUISITION_COST, 0)
        WHEN COALESCE(CRFX.CUARAT, 0) > 0
            THEN COALESCE(BASE.LAYER_QTY_CALC, 0)
                 * COALESCE(BASE.ACQUISITION_COST, 0)
                 * {{BOOK_RATE:BASE.TRN_CURRENCY}}
                 / CRFX.CUARAT
        ELSE COALESCE(BASE.LAYER_QTY_CALC, 0)
             * COALESCE(BASE.ACQUISITION_COST, 0)
    END                                         AS STOCK_VALUE_AT_CURRENT_RATE,

    BASE.SUPPLIER_CODE,
    BASE.SUPPLIER_NAME,
    BASE.TRANSACTION_DATE,
    BASE.TRANSACTION_TYPE,
    BASE.INVOICE_NUMBER,

    COALESCE(BASE.STANDARD_COST, 0)             AS STANDARD_COST,
    -- [CHANGED] first row per item only, like SOH_QUANTITY. The original repeated
    -- SOH x standard cost on every layer row, so totalling the column overstated it.
    CASE
        WHEN BASE.ITEM_ROW_NUM = 1
            THEN COALESCE(BASE.SOH_QUANTITY, 0) * COALESCE(BASE.STANDARD_COST, 0)
        ELSE NULL
    END                                         AS MOVEX_STOCK_VALUE,

    BASE.ENTRY_DATE,
    BASE.ENTRY_TIME,
    BASE.TIME_SUFFIX,

    -- [CHANGED] new column: how this row was valued
    CASE
        WHEN COALESCE(BASE.TRN_CURRENCY, '') = 'AUD'  THEN 'AUD'
        WHEN COALESCE(AVFX.CUARAT, 0) > 0             THEN 'Spot rate'
        ELSE 'No rate - book cost used'
    END                                         AS VALUE_SOURCE

FROM (
    SELECT
        L.WAREHOUSE,
        L.ITEM_NO,
        L.ITEM_DESCRIPTION,
        L.SOH_QUANTITY,
        L.ACQUISITION_COST,
        L.NEW_AVG_COST,
        L.ITEM_CURRENCY,
        L.TRN_CURRENCY,
        L.ORDER_NUMBER,
        L.TRANSACTION_QTY,
        L.SUPPLIER_CODE,
        L.SUPPLIER_NAME,
        L.TRANSACTION_DATE,
        L.TRANSACTION_TYPE,
        L.INVOICE_NUMBER,
        L.STANDARD_COST,
        L.ENTRY_DATE,
        L.ENTRY_TIME,
        L.TIME_SUFFIX,
        ROW_NUMBER() OVER (
            PARTITION BY L.WAREHOUSE, L.ITEM_NO
            ORDER BY L.ENTRY_DATE DESC,
                     L.ENTRY_TIME DESC,
                     L.TIME_SUFFIX DESC
        )                                       AS ITEM_ROW_NUM,
        SUM(L.TRANSACTION_QTY) OVER (
            PARTITION BY L.WAREHOUSE, L.ITEM_NO
            ORDER BY L.ENTRY_DATE DESC,
                     L.ENTRY_TIME DESC,
                     L.TIME_SUFFIX DESC
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        )                                       AS RUNNING_TOTAL,
        COALESCE(
            SUM(L.TRANSACTION_QTY) OVER (
                PARTITION BY L.WAREHOUSE, L.ITEM_NO
                ORDER BY L.ENTRY_DATE DESC,
                         L.ENTRY_TIME DESC,
                         L.TIME_SUFFIX DESC
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
            ), 0
        )                                       AS RUNNING_TOTAL_BEFORE,
        CASE
            WHEN COALESCE(
                     SUM(L.TRANSACTION_QTY) OVER (
                         PARTITION BY L.WAREHOUSE, L.ITEM_NO
                         ORDER BY L.ENTRY_DATE DESC,
                                  L.ENTRY_TIME DESC,
                                  L.TIME_SUFFIX DESC
                         ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                     ), 0
                 ) <= COALESCE(L.SOH_QUANTITY, 0)
                THEN COALESCE(L.TRANSACTION_QTY, 0)
            ELSE COALESCE(L.TRANSACTION_QTY, 0)
                 - (COALESCE(
                        SUM(L.TRANSACTION_QTY) OVER (
                            PARTITION BY L.WAREHOUSE, L.ITEM_NO
                            ORDER BY L.ENTRY_DATE DESC,
                                     L.ENTRY_TIME DESC,
                                     L.TIME_SUFFIX DESC
                            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                        ), 0
                    ) - COALESCE(L.SOH_QUANTITY, 0))
        END                                     AS LAYER_QTY_CALC
    FROM (
        SELECT
            IB.MBWHLO                           AS WAREHOUSE,
            IB.MBITNO                           AS ITEM_NO,
            RTRIM(LTRIM(ITM.MMITDS))            AS ITEM_DESCRIPTION,
            IB.MBSTQT                           AS SOH_QUANTITY,
            COALESCE(AV.A7MFCO, 0)              AS ACQUISITION_COST,
            COALESCE(AV.A7APPR, 0)              AS NEW_AVG_COST,
            COALESCE(ITM.MMCUCD, '')            AS ITEM_CURRENCY,
            AV.A7RIDN                           AS ORDER_NUMBER,
            COALESCE(AV.A7TRQT, 0)              AS TRANSACTION_QTY,
            COALESCE(AV.A7SUNO, '')             AS SUPPLIER_CODE,
            COALESCE(RTRIM(LTRIM(SM.IDSUNM)),'') AS SUPPLIER_NAME,
            AV.A7TRDT                           AS TRANSACTION_DATE,
            AV.A7GTYP                           AS TRANSACTION_TYPE,
            COALESCE(AV.A7SINO, '')             AS INVOICE_NUMBER,
            COALESCE(STD.KOCSU1, 0)             AS STANDARD_COST,
            CASE
                WHEN AV.A7GTYP = 'IM'
                     AND COALESCE(PU.IHCUCD, '') <> ''
                    THEN PU.IHCUCD
                WHEN AV.A7GTYP = '20'
                     AND COALESCE(PH.IACUCD, '') <> ''
                    THEN PH.IACUCD
                ELSE COALESCE(ITM.MMCUCD, '')
            END                                 AS TRN_CURRENCY,
            AV.A7RGDT                           AS ENTRY_DATE,
            AV.A7RGTM                           AS ENTRY_TIME,
            AV.A7TMSX                           AS TIME_SUFFIX
        FROM {{SCHEMA}}.MITBAL IB
        LEFT JOIN {{SCHEMA}}.MITMAS ITM
               ON ITM.MMCONO = IB.MBCONO
              AND ITM.MMITNO = IB.MBITNO
        -- Standard cost from MCHEAD, latest KOPCDT per item.
        -- [SOURCE REVISION 2026-10-16, Karen Lewin] cost type 3 (was 2). Purchased items use
        -- KOCSU1; finished goods use KOCSU4 where KOCSU1 is zero.
        LEFT JOIN (
            SELECT KOCONO, KOFACI, KOITNO,
                   COALESCE(NULLIF(KOCSU1, 0), KOCSU4) AS KOCSU1
            FROM (
                SELECT KOCONO, KOFACI, KOITNO, KOCSU1, KOCSU4,
                       ROW_NUMBER() OVER (
                           PARTITION BY KOCONO, KOFACI, KOITNO
                           ORDER BY KOPCDT DESC
                       ) AS RN
                FROM {{SCHEMA}}.MCHEAD
                WHERE KOCONO = {{CONO}}
                  AND KOFACI = 'D'
                  AND KOPCTP  = 3
                  AND (KOCSU1 > 0 OR KOCSU4 > 0)
            ) S
            WHERE RN = 1
        ) STD
               ON STD.KOCONO = IB.MBCONO
              AND STD.KOFACI = IB.MBFACI
              AND STD.KOITNO = IB.MBITNO
        LEFT JOIN {{SCHEMA}}.FCAAVP AV
                ON AV.A7CONO = IB.MBCONO
               AND AV.A7FACI = IB.MBFACI
               AND AV.A7ITNO = IB.MBITNO
               AND AV.A7TRQT > 0
        LEFT JOIN {{SCHEMA}}.CIDMAS SM
               ON SM.IDCONO = IB.MBCONO
              AND SM.IDSUNO  = AV.A7SUNO
        LEFT JOIN {{SCHEMA}}.MPHEAD PH
               ON PH.IACONO = IB.MBCONO
              AND PH.IAPUNO = AV.A7RIDN
              AND AV.A7GTYP = '20'
        LEFT JOIN (
            SELECT IHCONO, IHPUNO, IHSUNO, IHCUCD,
                   ROW_NUMBER() OVER (
                       PARTITION BY IHCONO, IHPUNO, IHSUNO
                       ORDER BY IHIVDT DESC, IHLMDT DESC
                   ) AS RN
            FROM {{SCHEMA}}.MPURST
            WHERE IHCONO = {{CONO}}
        ) PU
               ON PU.IHCONO = IB.MBCONO
              AND PU.IHPUNO  = AV.A7RIDN
              AND PU.IHSUNO  = AV.A7SUNO
              AND PU.RN      = 1
              AND AV.A7GTYP  = 'IM'
        WHERE IB.MBCONO = {{CONO}}
          AND IB.MBFACI = 'D'
          AND IB.MBWHLO IN ({{WAREHOUSES}})
          AND IB.MBSTAT = '20'
          AND IB.MBSTQT > 0
          AND AV.A7TRDT IS NOT NULL
          {{FILTERS}}
    ) L
) BASE

LEFT JOIN {{SCHEMA}}.CCURRA AVFX
       ON AVFX.CUCONO = {{CONO}}
      AND AVFX.CUDIVI = '{{RATE_DIVISION}}'
      AND AVFX.CUCRTP = '{{SPOT_RATE_TYPE}}'
      AND AVFX.CUCUCD = BASE.TRN_CURRENCY
      AND AVFX.CUCUTD = (
              SELECT MAX(FX7.CUCUTD)
              FROM {{SCHEMA}}.CCURRA FX7
              WHERE FX7.CUCONO = {{CONO}}
                AND FX7.CUDIVI = '{{RATE_DIVISION}}'
                AND FX7.CUCRTP = '{{SPOT_RATE_TYPE}}'
                AND FX7.CUCUCD = BASE.TRN_CURRENCY
                AND FX7.CUCUTD <= CASE
                                      WHEN COALESCE(BASE.ENTRY_DATE, 0) > 0
                                          THEN BASE.ENTRY_DATE
                                      ELSE 99999999
                                  END
          )

-- Revaluation rate: latest rate, or [CHANGED] latest on/before the chosen date
LEFT JOIN {{SCHEMA}}.CCURRA CRFX
       ON CRFX.CUCONO = {{CONO}}
      AND CRFX.CUDIVI = '{{RATE_DIVISION}}'
      AND CRFX.CUCRTP = '{{SPOT_RATE_TYPE}}'
      AND CRFX.CUCUCD = BASE.TRN_CURRENCY
      AND CRFX.CUCUTD = (
              SELECT MAX(CR2.CUCUTD)
              FROM {{SCHEMA}}.CCURRA CR2
              WHERE CR2.CUCONO = {{CONO}}
                AND CR2.CUDIVI = '{{RATE_DIVISION}}'
                AND CR2.CUCRTP = '{{SPOT_RATE_TYPE}}'
                AND CR2.CUCUCD = BASE.TRN_CURRENCY
                {{REVALUE_DATE:CR2}}
          )

WHERE BASE.RUNNING_TOTAL_BEFORE < BASE.SOH_QUANTITY

UNION ALL

-- PART B: items with SOH > 0 but no FCAAVP history — valued at MITMAS purchase price
SELECT
    IB.MBWHLO                               AS WAREHOUSE,
    IB.MBITNO                               AS ITEM_NO,
    RTRIM(LTRIM(ITM.MMITDS))                AS ITEM_DESCRIPTION,
    IB.MBSTQT                               AS SOH_QUANTITY,
    IB.MBSTQT                               AS LAYER_QTY,
    CASE
        WHEN COALESCE(ITM.MMCUCD, '') = 'AUD'
            THEN COALESCE(ITM.MMPUPR, 0)
        WHEN COALESCE(FBFX.CUARAT, 0) > 0
            THEN COALESCE(ITM.MMPUPR, 0)
                 * {{BOOK_RATE:COALESCE(ITM.MMCUCD, '')}}
                 / FBFX.CUARAT
        ELSE COALESCE(ITM.MMPUPR, 0)
    END                                     AS AUD_NEW_AVG_COST_SPOT_RATE,
    COALESCE(ITM.MMPUPR, 0)                 AS ACQUISITION_COST,
    COALESCE(ITM.MMPUPR, 0)                 AS AUD_NEW_AVG_COST_BOOK_RATE,
    COALESCE(ITM.MMCUCD, '')                AS TRN_CURRENCY,
    COALESCE(FBFX.CUARAT, 0)                AS SPOT_RATE_AT_ENTRY_DATE,
    CAST(NULL AS VARCHAR(15))               AS ORDER_NUMBER,
    CAST(NULL AS DECIMAL(17,4))             AS TRANSACTION_QTY,
    CASE
        WHEN COALESCE(ITM.MMCUCD, '') = 'AUD'
            THEN IB.MBSTQT * COALESCE(ITM.MMPUPR, 0)
        WHEN COALESCE(FBFX.CUARAT, 0) > 0
            THEN IB.MBSTQT
                 * COALESCE(ITM.MMPUPR, 0)
                 * {{BOOK_RATE:COALESCE(ITM.MMCUCD, '')}}
                 / FBFX.CUARAT
        ELSE IB.MBSTQT * COALESCE(ITM.MMPUPR, 0)
    END                                     AS STOCK_VALUE_AT_ACQUISITION_RATE,
    COALESCE(CUFX.CUARAT, 0)                AS CURRENT_SPOT_RATE,
    CASE
        WHEN COALESCE(ITM.MMCUCD, '') = 'AUD'
            THEN IB.MBSTQT * COALESCE(ITM.MMPUPR, 0)
        WHEN COALESCE(CUFX.CUARAT, 0) > 0
            THEN IB.MBSTQT
                 * COALESCE(ITM.MMPUPR, 0)
                 * {{BOOK_RATE:COALESCE(ITM.MMCUCD, '')}}
                 / CUFX.CUARAT
        ELSE IB.MBSTQT * COALESCE(ITM.MMPUPR, 0)
    END                                     AS STOCK_VALUE_AT_CURRENT_RATE,
    CAST(NULL AS VARCHAR(10))               AS SUPPLIER_CODE,
    CAST(NULL AS VARCHAR(36))               AS SUPPLIER_NAME,
    CAST(NULL AS INTEGER)                   AS TRANSACTION_DATE,
    CAST(NULL AS VARCHAR(2))                AS TRANSACTION_TYPE,
    CAST(NULL AS VARCHAR(15))               AS INVOICE_NUMBER,
    -- [CHANGED] STANDARD_COST and MOVEX_STOCK_VALUE moved before ENTRY_DATE to match
    -- Part A. UNION ALL pairs columns by position; in the original they landed under
    -- ENTRY_TIME / TIME_SUFFIX and the real columns came out blank.
    COALESCE(STD.KOCSU1, 0)                 AS STANDARD_COST,
    IB.MBSTQT
        * COALESCE(STD.KOCSU1, 0)           AS MOVEX_STOCK_VALUE,
    CAST(NULL AS INTEGER)                   AS ENTRY_DATE,
    CAST(NULL AS INTEGER)                   AS ENTRY_TIME,
    CAST(NULL AS INTEGER)                   AS TIME_SUFFIX,
    -- [CHANGED] new column
    'No history - purchase price'           AS VALUE_SOURCE

FROM {{SCHEMA}}.MITBAL IB

LEFT JOIN {{SCHEMA}}.MITMAS ITM
       ON ITM.MMCONO = IB.MBCONO
      AND ITM.MMITNO = IB.MBITNO

-- Standard cost from MCHEAD, latest KOPCDT per item.
-- [SOURCE REVISION 2026-10-16, Karen Lewin] cost type 3 (was 2). Purchased items use
-- KOCSU1; finished goods use KOCSU4 where KOCSU1 is zero.
LEFT JOIN (
    SELECT KOCONO, KOFACI, KOITNO,
           COALESCE(NULLIF(KOCSU1, 0), KOCSU4) AS KOCSU1
    FROM (
        SELECT KOCONO, KOFACI, KOITNO, KOCSU1, KOCSU4,
               ROW_NUMBER() OVER (
                   PARTITION BY KOCONO, KOFACI, KOITNO
                   ORDER BY KOPCDT DESC
               ) AS RN
        FROM {{SCHEMA}}.MCHEAD
        WHERE KOCONO = {{CONO}}
          AND KOFACI = 'D'
          AND KOPCTP  = 3
          AND (KOCSU1 > 0 OR KOCSU4 > 0)
    ) S
    WHERE RN = 1
) STD
       ON STD.KOCONO = IB.MBCONO
      AND STD.KOFACI = IB.MBFACI
      AND STD.KOITNO = IB.MBITNO

LEFT JOIN {{SCHEMA}}.CCURRA FBFX
       ON FBFX.CUCONO = {{CONO}}
      AND FBFX.CUDIVI = '{{RATE_DIVISION}}'
      AND FBFX.CUCRTP = '{{SPOT_RATE_TYPE}}'
      AND FBFX.CUCUCD = ITM.MMCUCD
      AND FBFX.CUCUTD = (
              SELECT MAX(FB2.CUCUTD)
              FROM {{SCHEMA}}.CCURRA FB2
              WHERE FB2.CUCONO = {{CONO}}
                AND FB2.CUDIVI = '{{RATE_DIVISION}}'
                AND FB2.CUCRTP = '{{SPOT_RATE_TYPE}}'
                AND FB2.CUCUCD = ITM.MMCUCD
          )

LEFT JOIN {{SCHEMA}}.CCURRA CUFX
       ON CUFX.CUCONO = {{CONO}}
      AND CUFX.CUDIVI = '{{RATE_DIVISION}}'
      AND CUFX.CUCRTP = '{{SPOT_RATE_TYPE}}'
      AND CUFX.CUCUCD = ITM.MMCUCD
      AND CUFX.CUCUTD = (
              SELECT MAX(CU2.CUCUTD)
              FROM {{SCHEMA}}.CCURRA CU2
              WHERE CU2.CUCONO = {{CONO}}
                AND CU2.CUDIVI = '{{RATE_DIVISION}}'
                AND CU2.CUCRTP = '{{SPOT_RATE_TYPE}}'
                AND CU2.CUCUCD = ITM.MMCUCD
                {{REVALUE_DATE:CU2}}
          )

WHERE IB.MBCONO = {{CONO}}
  AND IB.MBFACI = 'D'
  AND IB.MBWHLO IN ({{WAREHOUSES}})
  AND IB.MBSTAT = '20'
  AND IB.MBSTQT > 0
  AND NOT EXISTS (
          SELECT 1
          FROM {{SCHEMA}}.FCAAVP AV2
          WHERE AV2.A7CONO = IB.MBCONO
            AND AV2.A7FACI = IB.MBFACI
            AND AV2.A7ITNO = IB.MBITNO
            AND AV2.A7TRQT > 0
      )
  {{FILTERS}}

ORDER BY
    WAREHOUSE,
    ITEM_NO,
    ENTRY_DATE DESC,
    ENTRY_TIME DESC,
    TIME_SUFFIX DESC
{{ROW_CAP}}
