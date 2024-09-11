namespace MoodleExtraction.Models;
public class CourseJson
{
    public string? CourseName { get; set; }
    public string? Description { get; set; } 
    public string? CourseId { get; set; }
    public string? id { get; set; }
    public string? Photo { get; set; }
    //public string Progession { get; set; } = "0";
    //public string Status { get; set; } = "en cours";
    public string? ProfessorId { get; set; }
    public string? CodeNiveau { get; set; }
    public string? CodeClasse { get; set; }
    public bool IsDownloaded { get; set; } = false;
    public List<ElementProgramme>? ElementProgrammes { get; set; }
    public List<Element>? Elements { get; set; }
}

public class ElementProgramme
{
    public int Level { get; set; }
    public string? Code { get; set; }
}

public class Element
{
    public string? ElementName { get; set; }
    public string? ElementId { get; set; }
    public string? CourseId { get; set; }
    public string? IconBody { get; set; }
    public string? Link { get; set; }
    public bool IsDownloaded { get; set; }
    public List<Content>? contents { get; set; }
}

public class Content
{
    public string? ContentName { get; set; }
    public string? Type { get; set; }
    public string? Width { get; set; }
    public string? Height { get; set; }
    public FilesModel? Files { get; set; }
}

public class FilesModel {
    public string FileHtml { get; set; } = default!;
    public string FileTxt { get; set; } = default!;
    public string FilePdf { get; set; } = default!;
    public string FileMp4 { get; set; } = default!;
    public List<string> FilesH5p { get; set; } = new List<string>();
}
public class ProcessCoursesRequest
{
    public string CoursesPath { get; set; }
    public string H5PPath { get; set; }
}
