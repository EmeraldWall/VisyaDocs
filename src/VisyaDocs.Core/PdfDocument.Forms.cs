using System.Runtime.InteropServices;
using VisyaDocs.Core.Interop;

namespace VisyaDocs.Core;

public enum FormFieldKind
{
    Unknown = 0,
    PushButton = 1,
    CheckBox = 2,
    RadioButton = 3,
    ComboBox = 4,
    ListBox = 5,
    Text = 6,
    Signature = 7,
}

/// <summary>An interactive form field widget on a page.</summary>
public sealed record FormField(
    int PageIndex,
    int AnnotIndex,
    FormFieldKind Kind,
    string Name,
    string Value,
    bool IsChecked,
    bool ReadOnly,
    bool Required,
    bool Multiline,
    IReadOnlyList<string> Options,
    int SelectedIndex,
    PdfRect Bounds);

// AcroForm filling through PDFium's form fill environment, which also regenerates field appearances.
public sealed unsafe partial class PdfDocument
{
    private const int FormInfoSize = 512;

    private nint _form;
    private void* _formInfo;

    /// <summary>True when the document contains an interactive (AcroForm) form.</summary>
    public bool HasForm
    {
        get { lock (Sync) return _form != 0; }
    }

    private void InitFormsLocked()
    {
        if (_doc == 0 || Pdfium.FPDF_GetFormType(_doc) != Pdfium.FORMTYPE_ACRO_FORM) return;
        // Version 1 of FPDF_FORMFILLINFO with every callback left null: PDFium checks each one
        // before calling it, and JavaScript stays disabled. The block must outlive the environment.
        _formInfo = NativeMemory.AllocZeroed(FormInfoSize);
        *(int*)_formInfo = 1;
        _form = Pdfium.FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        if (_form == 0)
        {
            NativeMemory.Free(_formInfo);
            _formInfo = null;
        }
    }

    private void ExitFormsLocked()
    {
        if (_form != 0) Pdfium.FPDFDOC_ExitFormFillEnvironment(_form);
        if (_formInfo != null) NativeMemory.Free(_formInfo);
        _form = 0;
        _formInfo = null;
    }

    /// <summary>All form fields on a page, in the page's annotation order.</summary>
    public IReadOnlyList<FormField> GetFormFields(int pageIndex) => WithPage(pageIndex, page =>
    {
        var fields = new List<FormField>();
        if (_form == 0) return fields;
        int count = Pdfium.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annot = Pdfium.FPDFPage_GetAnnot(page, i);
            if (annot == 0) continue;
            try
            {
                if (Pdfium.FPDFAnnot_GetSubtype(annot) != Pdfium.FPDF_ANNOT_WIDGET) continue;
                fields.Add(ReadField(pageIndex, i, annot));
            }
            finally
            {
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
        }
        return fields;
    });

    private FormField ReadField(int pageIndex, int index, nint annot)
    {
        nint form = _form;
        var kind = (FormFieldKind)Pdfium.FPDFAnnot_GetFormFieldType(form, annot);
        if (!Enum.IsDefined(kind)) kind = FormFieldKind.Unknown;
        int flags = Pdfium.FPDFAnnot_GetFormFieldFlags(form, annot);
        string name = Pdfium.ReadUtf16((p, n) => Pdfium.FPDFAnnot_GetFormFieldName(form, annot, (char*)p, new CULong((nuint)n)).Value);
        string value = Pdfium.ReadUtf16((p, n) => Pdfium.FPDFAnnot_GetFormFieldValue(form, annot, (char*)p, new CULong((nuint)n)).Value);

        var options = new List<string>();
        int selected = -1;
        if (kind is FormFieldKind.ComboBox or FormFieldKind.ListBox)
        {
            int optionCount = Math.Max(0, Pdfium.FPDFAnnot_GetOptionCount(form, annot));
            for (int o = 0; o < optionCount; o++)
            {
                int option = o;
                options.Add(Pdfium.ReadUtf16((p, n) => Pdfium.FPDFAnnot_GetOptionLabel(form, annot, option, (char*)p, new CULong((nuint)n)).Value));
                if (selected < 0 && Pdfium.FPDFAnnot_IsOptionSelected(form, annot, o) != 0) selected = o;
            }
        }

        bool isChecked = kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton && Pdfium.FPDFAnnot_IsChecked(form, annot) != 0;
        Pdfium.FS_RECTF r;
        Pdfium.FPDFAnnot_GetRect(annot, &r);
        return new FormField(pageIndex, index, kind, name, value, isChecked,
            (flags & Pdfium.FPDF_FORMFLAG_READONLY) != 0,
            (flags & Pdfium.FPDF_FORMFLAG_REQUIRED) != 0,
            (flags & Pdfium.FPDF_FORMFLAG_TEXT_MULTILINE) != 0,
            options, selected,
            new PdfRect(Math.Min(r.left, r.right), Math.Min(r.bottom, r.top), Math.Max(r.left, r.right), Math.Max(r.bottom, r.top)));
    }

    /// <summary>Sets the text of a text field (or the edit text of an editable combo box).</summary>
    public void SetFieldText(int pageIndex, int annotIndex, string text) => WithField(pageIndex, annotIndex, (page, annot) =>
    {
        if (Pdfium.FORM_SetFocusedAnnot(_form, annot) == 0) throw new PdfException("The field cannot be edited.");
        Pdfium.FORM_SelectAllText(_form, page);
        Pdfium.FORM_ReplaceSelection(_form, page, text);
    });

    /// <summary>Toggles a check box or selects a radio button, as a click on it would.</summary>
    public void ToggleCheck(int pageIndex, int annotIndex) => WithField(pageIndex, annotIndex, (page, annot) =>
    {
        Pdfium.FS_RECTF r;
        Pdfium.FPDFAnnot_GetRect(annot, &r);
        double x = (r.left + r.right) / 2, y = (r.top + r.bottom) / 2;
        Pdfium.FORM_OnLButtonDown(_form, page, 0, x, y);
        Pdfium.FORM_OnLButtonUp(_form, page, 0, x, y);
    });

    /// <summary>Selects an option of a combo box or list box.</summary>
    public void SelectOption(int pageIndex, int annotIndex, int optionIndex) => WithField(pageIndex, annotIndex, (page, annot) =>
    {
        if (Pdfium.FORM_SetFocusedAnnot(_form, annot) == 0 || Pdfium.FORM_SetIndexSelected(_form, page, optionIndex, 1) == 0)
            throw new PdfException("The option could not be selected.");
    });

    private void WithField(int pageIndex, int annotIndex, Action<nint, nint> edit) => Mutate(() => WithPage(pageIndex, page =>
    {
        if (_form == 0) throw new PdfException("This document has no form.");
        nint annot = Pdfium.FPDFPage_GetAnnot(page, annotIndex);
        if (annot == 0) throw new PdfException("The form field can no longer be found.");
        try
        {
            edit(page, annot);
        }
        finally
        {
            Pdfium.FORM_ForceToKillFocus(_form);
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
        return 0;
    }));
}
