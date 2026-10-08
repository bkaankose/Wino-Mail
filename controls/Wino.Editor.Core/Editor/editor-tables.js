(function () {
    "use strict";

    function currentCell() {
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) return null;
        const node = selection.anchorNode;
        const element = node && (node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement);
        return element && element.closest("td,th");
    }

    function cellMarkup() {
        return '<td style="min-width:72px;padding:6px;border:1px solid #a0a0a0;vertical-align:top;"><br></td>';
    }

    function insertTable(rows, columns) {
        const rowCount = Math.max(1, Math.min(20, Number(rows) || 1));
        const columnCount = Math.max(1, Math.min(20, Number(columns) || 1));
        const row = `<tr>${cellMarkup().repeat(columnCount)}</tr>`;
        const table = `<table style="border-collapse:collapse;width:100%;"><tbody>${row.repeat(rowCount)}</tbody></table><p><br></p>`;
        return window.WinoEditor.insertHtml(table);
    }

    function styleCell(cell) {
        cell.style.minWidth = "72px";
        cell.style.padding = "6px";
        cell.style.border = "1px solid #a0a0a0";
        cell.style.verticalAlign = "top";
        cell.innerHTML = "<br>";
    }

    function addRow(cell, table) {
        const row = cell.parentElement;
        const newRow = table.insertRow(row.rowIndex + 1);
        const count = Math.max(1, row.cells.length);
        for (let index = 0; index < count; index += 1) styleCell(newRow.insertCell());
    }

    function removeRow(cell, table) {
        if (table.rows.length <= 1) table.remove();
        else cell.parentElement.remove();
    }

    function addColumn(cell, table) {
        const index = cell.cellIndex + 1;
        Array.from(table.rows).forEach(row => styleCell(row.insertCell(Math.min(index, row.cells.length))));
    }

    function removeColumn(cell, table) {
        const index = cell.cellIndex;
        Array.from(table.rows).forEach(row => {
            if (index < row.cells.length) row.deleteCell(index);
        });
        if (Array.from(table.rows).every(row => row.cells.length === 0)) table.remove();
    }

    function command(name) {
        window.WinoEditor.restoreSelection();
        const cell = currentCell();
        const table = cell && cell.closest("table");
        if (!cell || !table) return false;

        if (name === "addRow") addRow(cell, table);
        else if (name === "removeRow") removeRow(cell, table);
        else if (name === "addColumn") addColumn(cell, table);
        else if (name === "removeColumn") removeColumn(cell, table);
        else if (name === "deleteTable") table.remove();
        else return false;

        window.WinoEditor.notifyContentChanged();
        return true;
    }

    window.WinoEditorTables = { insertTable, command };
}());
